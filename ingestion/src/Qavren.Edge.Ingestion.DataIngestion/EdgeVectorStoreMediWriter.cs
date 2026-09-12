using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion.DataIngestion;

/// <summary>
/// Writes MEDI chunks into an SP2 collection shaped by <see cref="IngestionSchema.BuildDefinition"/>
/// (spec 11, plan task 6.3 step 4), so a MEDI pipeline and SP3's own <see cref="IIngestionPipeline"/>
/// can share one collection and one search.
/// <para>
/// <b>Content-hash incremental re-index is NOT available on this path.</b> MEDI's model has nowhere
/// to put a document hash, a recipe hash or a state row, so this writer cannot know whether a
/// document changed. Every <see cref="WriteAsync"/> is therefore a <b>full rewrite</b> of each
/// document it sees: every chunk is embedded again and upserted, and any previously stored chunk of
/// that document that the new set does not contain is deleted afterwards. On a phone that is the
/// difference between a run that skips ninety-nine unchanged files and one that re-embeds all
/// hundred — <see cref="IIngestionPipeline"/> is what a device should use; this type exists so the
/// MEDI ecosystem can reach an SP2 collection at all.
/// </para>
/// <para>
/// Rows are written through <see cref="IngestedChunk.ToRecord"/> with the identity SP3 derives
/// (spec 9.3: source id, document id, the embed-text hash and a duplicate ordinal), so a document
/// written here and later re-indexed by <see cref="IIngestionPipeline"/> diffs cleanly instead of
/// duplicating. Chunks that came through <see cref="EdgeChunkerMediAdapter"/> carry their offsets,
/// token count, kind and page; chunks from any other MEDI chunker get <c>-1</c> offsets, a token
/// count from the optional tokenizer (or 0), <see cref="DocumentBlockKind.Paragraph"/> and page
/// <c>-1</c>. The embedded text is <see cref="EdgeChunkerMediAdapter.EmbedTextKey"/> when present
/// and the chunk's <c>Content</c> otherwise — MEDI's own writer embeds <c>Content</c> too.
/// </para>
/// <para>
/// Additions land before deletions, per document, so a failure mid-write leaves the old chunks in
/// place rather than none. Nothing here opens a transaction around a collection call (spec 9.5's
/// invariant): the stored-key read is one connection, each upsert is SP2's own transaction, and the
/// stale-key delete is SP2's <c>DeleteAsync</c>.
/// </para>
/// </summary>
public sealed class EdgeVectorStoreMediWriter : IngestionChunkWriter<string>
{
    private readonly MEVD.VectorStoreCollection<object, Dictionary<string, object?>> _collection;
    private readonly IEdgeDatabase _database;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly IChunkTokenizer? _tokenizer;
    private readonly string _sourceId;
    private readonly string _dataTable;
    private readonly int _batchSize;
    private readonly TimeProvider _time;

    /// <summary>Creates the writer.</summary>
    /// <param name="collection">
    /// An SP2 dynamic collection over the ingestion schema — what <c>EdgeVectorStore.GetDynamicCollection</c>
    /// returns for <see cref="IngestionSchema.BuildDefinition"/>. Its data table and database are
    /// resolved through <c>GetService</c>, so any other provider's collection is refused.
    /// </param>
    /// <param name="generator">The embedding generator. Called once per batch, outside any transaction.</param>
    /// <param name="sourceId">The source id every row carries; the identity's first input.</param>
    /// <param name="tokenizer">Counts tokens for chunks that carry no count. Null stores 0 for them.</param>
    /// <param name="batchSize">Chunks per embed-and-upsert pair. 32 matches <c>IngestionOptions.WriteBatchSize</c>.</param>
    /// <param name="timeProvider">Stamps <c>created_utc</c>. Null is the system clock.</param>
    public EdgeVectorStoreMediWriter(
        MEVD.VectorStoreCollection<object, Dictionary<string, object?>> collection,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string sourceId,
        IChunkTokenizer? tokenizer = null,
        int batchSize = 32,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        _collection = collection;
        _generator = generator;
        _sourceId = sourceId;
        _tokenizer = tokenizer;
        _batchSize = batchSize;
        _time = timeProvider ?? TimeProvider.System;

        _database = collection.GetService(typeof(IEdgeDatabase)) as IEdgeDatabase
            ?? throw new ArgumentException(
                "The collection is not a Qavren.Edge.VectorData collection: it did not expose IEdgeDatabase through " +
                "GetService. EdgeVectorStoreMediWriter writes SP2 collections only.",
                nameof(collection));

        _dataTable = (collection.GetService(typeof(EdgeVectorSchema)) as EdgeVectorSchema)?.DataTable
            ?? collection.Name;
    }

    /// <summary>The source id every written row carries.</summary>
    public string SourceId => _sourceId;

    /// <inheritdoc />
    public override async Task WriteAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var documents = new Dictionary<string, DocumentState>(StringComparer.Ordinal);
        var createdUtc = _time.GetUtcNow();

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var documentId = chunk.Document.Identifier;
            if (!documents.TryGetValue(documentId, out var state))
            {
                state = new DocumentState(
                    documentId,
                    await ReadStoredKeysAsync(documentId, cancellationToken).ConfigureAwait(false));
                documents.Add(documentId, state);
            }

            state.Pending.Add(chunk);
            if (state.Pending.Count >= _batchSize)
            {
                await FlushAsync(state, createdUtc, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var state in documents.Values)
        {
            await FlushAsync(state, createdUtc, cancellationToken).ConfigureAwait(false);

            var stale = state.StoredKeys.Where(k => !state.WrittenKeys.Contains(k)).Cast<object>().ToList();
            if (stale.Count != 0)
            {
                await _collection.DeleteAsync(stale, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The collection and the generator are the caller's; this writer owns nothing disposable.
        base.Dispose(disposing);
    }

    private async Task FlushAsync(DocumentState state, DateTimeOffset createdUtc, CancellationToken cancellationToken)
    {
        if (state.Pending.Count == 0)
        {
            return;
        }

        var window = state.Pending.ToList();
        state.Pending.Clear();

        var embedTexts = new string[window.Count];
        for (var i = 0; i < window.Count; i++)
        {
            embedTexts[i] = EmbedTextOf(window[i]);
        }

        var embeddings = await _generator.GenerateAsync(embedTexts, options: null, cancellationToken)
            .ConfigureAwait(false);
        if (embeddings.Count != window.Count)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The embedding generator returned {0} vectors for {1} inputs.",
                    embeddings.Count,
                    window.Count));
        }

        var records = new List<Dictionary<string, object?>>(window.Count);
        for (var i = 0; i < window.Count; i++)
        {
            var chunk = window[i];
            var contentHash = ContentHash.OfText(embedTexts[i]);
            state.DuplicateOrdinals.TryGetValue(contentHash, out var duplicateOrdinal);
            state.DuplicateOrdinals[contentHash] = duplicateOrdinal + 1;

            var key = ContentHash.Combine(
                [
                    ContentHash.OfText(_sourceId),
                    ContentHash.OfText(state.DocumentId),
                    contentHash,
                    new ContentHash((UInt128)(uint)duplicateOrdinal),
                ]).ToHex();

            var ingested = new IngestedChunk(
                key,
                _sourceId,
                state.DocumentId,
                ReadInt(chunk, EdgeChunkerMediAdapter.OrdinalKey) ?? state.NextOrdinal,
                chunk.Content,
                chunk.Context,
                ReadInt(chunk, EdgeChunkerMediAdapter.CharStartKey) ?? -1,
                ReadInt(chunk, EdgeChunkerMediAdapter.CharEndKey) ?? -1,
                ReadInt(chunk, EdgeChunkerMediAdapter.TokenCountKey)
                    ?? _tokenizer?.CountTokens(embedTexts[i].AsSpan())
                    ?? 0,
                ReadInt(chunk, EdgeChunkerMediAdapter.PageKey) ?? -1,
                ReadString(chunk, EdgeChunkerMediAdapter.BlockKindKey) ?? nameof(DocumentBlockKind.Paragraph),
                RootFact(chunk.Document, EdgeDocumentConverter.ExtractorIdKey) ?? EdgeDocumentConverter.DefaultExtractorId,
                RootFact(chunk.Document, EdgeDocumentConverter.MediaTypeKey) ?? EdgeDocumentConverter.DefaultMediaType,
                contentHash,
                createdUtc);

            state.NextOrdinal++;
            state.WrittenKeys.Add(key);
            records.Add(new Dictionary<string, object?>(ingested.ToRecord(embeddings[i].Vector), StringComparer.Ordinal));
        }

        await _collection.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ReadStoredKeysAsync(string documentId, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var keys = await connection.QueryAsync(
                $"""
                 SELECT "{IngestionColumns.Key}" FROM "{_dataTable}"
                 WHERE "{IngestionColumns.SourceId}" = $s AND "{IngestionColumns.DocumentId}" = $d
                 """,
                reader => reader.GetString(0),
                [new SqliteParameter("$s", _sourceId), new SqliteParameter("$d", documentId)],
                cancellationToken).ConfigureAwait(false);

            return new HashSet<string>(keys, StringComparer.Ordinal);
        }
    }

    private static string EmbedTextOf(IngestionChunk<string> chunk) =>
        ReadString(chunk, EdgeChunkerMediAdapter.EmbedTextKey) ?? chunk.Content;

    private static string? ReadString(IngestionChunk<string> chunk, string key) =>
        chunk.HasMetadata && chunk.Metadata.TryGetValue(key, out var value) ? value as string : null;

    private static int? ReadInt(IngestionChunk<string> chunk, string key) =>
        chunk.HasMetadata && chunk.Metadata.TryGetValue(key, out var value) && value is int number ? number : null;

    private static string? RootFact(IngestionDocument document, string key)
    {
        if (document.Sections.Count == 0 || !document.Sections[0].HasMetadata)
        {
            return null;
        }

        return document.Sections[0].Metadata.TryGetValue(key, out var value) ? value as string : null;
    }

    private sealed class DocumentState(string documentId, HashSet<string> storedKeys)
    {
        public string DocumentId { get; } = documentId;

        public HashSet<string> StoredKeys { get; } = storedKeys;

        public HashSet<string> WrittenKeys { get; } = new(StringComparer.Ordinal);

        public List<IngestionChunk<string>> Pending { get; } = [];

        public Dictionary<ContentHash, int> DuplicateOrdinals { get; } = [];

        public int NextOrdinal { get; set; }
    }
}
