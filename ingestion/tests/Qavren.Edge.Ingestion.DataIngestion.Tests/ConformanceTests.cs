using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DataIngestion;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.DataIngestion.Tests;

/// <summary>
/// Spec 14.4: a REAL MEDI <see cref="IngestionPipeline{T}"/> over <see cref="EdgeChunkerMediAdapter"/>
/// and <see cref="EdgeVectorStoreMediWriter"/>, fed by the fifteen-line reader in
/// <see cref="MarkdownExtractorReader"/>, writes into SP3's collection and the result is searchable
/// through SP2. That is the only proof the ecosystem claim is true rather than aspirational.
/// </summary>
public sealed class ConformanceTests
{
    private const string Markdown = """
        # Alpha

        The quick brown fox jumps over the lazy dog.

        ## Beta

        Pack my box with five dozen liquor jugs.

        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Two tiny sections must stay two chunks, so the merge-up rule is off for this corpus.</summary>
    private static void NoMerge(ChunkOptions options)
    {
        options.MergeShortSections = false;
        options.MinTokens = 1;
    }

    /// <summary>MEDI reports per-document failures as results, not throws; surface them as the real cause.</summary>
    private static async Task RunAsync(IngestionPipeline<string> pipeline, FileInfo file)
    {
        await foreach (var result in pipeline.ProcessAsync([file], Token).ConfigureAwait(false))
        {
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"MEDI failed on '{result.DocumentId}'.", result.Exception);
            }
        }
    }

    [Fact]
    public async Task A_real_MEDI_pipeline_writes_into_the_SP3_collection_and_SP2_finds_it()
    {
        using var host = await MediTestHost.StartAsync();
        var reader = new MarkdownExtractorReader();
        var chunker = new EdgeChunkerMediAdapter(
            new MarkdownHeadingChunker(), host.ResolvedChunking(NoMerge), host.Tokenizer);
        using var writer = new EdgeVectorStoreMediWriter(
            host.Collection, host.Generator, sourceId: "medi", tokenizer: host.Tokenizer);

        var file = await WriteFileAsync(host, "alpha.md", Markdown);
        using (var pipeline = new IngestionPipeline<string>(reader, chunker, writer, options: null, host.LoggerFactory))
        {
            await RunAsync(pipeline, file);
        }

        var identifier = Assert.Single(reader.Identifiers);
        Assert.Equal(1, host.Generator.CallCount);

        // The rows carry SP3's schema, the identifier MEDI assigned, and our source id.
        var rows = await ReadRowsAsync(host.Database, identifier);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("medi", r.SourceId));
        Assert.Contains(rows, r => r.Text.Contains("quick brown fox", StringComparison.Ordinal) && r.Breadcrumb == "Alpha");
        Assert.Contains(rows, r => r.Text.Contains("liquor jugs", StringComparison.Ordinal) && r.Breadcrumb == "Alpha › Beta");
        Assert.All(rows, r => Assert.Equal("markdown", r.ExtractorId));
        Assert.All(rows, r => Assert.Equal(IngestionMediaTypes.Markdown, r.MediaType));

        // And SP2 searches it: KNN over the embed text's deterministic vector lands on the row.
        var target = rows.Single(r => r.Text.Contains("liquor jugs", StringComparison.Ordinal));
        var query = new ReadOnlyMemory<float>(RecordingEmbeddingGenerator.VectorFor("Alpha › Beta\n\n" + target.Text));
        var hits = new List<IngestedChunk>();
        await foreach (var hit in host.Collection.SearchAsync(query, top: 1, options: null, Token))
        {
            hits.Add(IngestedChunk.FromRecord(hit.Record));
        }

        var best = Assert.Single(hits);
        Assert.Equal(target.Key, best.Key);
        Assert.Equal(identifier, best.DocumentId);
    }

    [Fact]
    public async Task A_second_pass_is_a_full_rewrite_and_leaves_no_stale_row()
    {
        using var host = await MediTestHost.StartAsync();
        var reader = new MarkdownExtractorReader();
        var chunker = new EdgeChunkerMediAdapter(
            new MarkdownHeadingChunker(), host.ResolvedChunking(NoMerge), host.Tokenizer);
        using var writer = new EdgeVectorStoreMediWriter(host.Collection, host.Generator, sourceId: "medi");

        var file = await WriteFileAsync(host, "beta.md", Markdown);
        using var pipeline = new IngestionPipeline<string>(reader, chunker, writer, options: null, host.LoggerFactory);
        await RunAsync(pipeline, file);
        var before = await ReadRowsAsync(host.Database, reader.Identifiers[0]);
        Assert.Equal(2, before.Count);

        // Drop the second section and add a third: one chunk gone, one kept, one new.
        await File.WriteAllTextAsync(
            file.FullName,
            "# Alpha\n\nThe quick brown fox jumps over the lazy dog.\n\n## Gamma\n\nSphinx of black quartz, judge my vow.\n",
            Token);
        await RunAsync(pipeline, file);

        var after = await ReadRowsAsync(host.Database, reader.Identifiers[0]);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, r => r.Text.Contains("liquor jugs", StringComparison.Ordinal));
        Assert.Contains(after, r => r.Text.Contains("black quartz", StringComparison.Ordinal));

        // The unchanged chunk keeps SP3's identity - the same key IIngestionPipeline would derive.
        var keptBefore = before.Single(r => r.Text.Contains("quick brown fox", StringComparison.Ordinal));
        var keptAfter = after.Single(r => r.Text.Contains("quick brown fox", StringComparison.Ordinal));
        Assert.Equal(keptBefore.Key, keptAfter.Key);

        // Full rewrite, as documented: BOTH passes embedded every chunk. No hash gate skipped anything.
        Assert.Equal(2, host.Generator.CallCount);
        Assert.Equal(2, host.Generator.Calls[1].Count);
    }

    [Fact]
    public async Task The_writer_refuses_a_collection_that_is_not_SP2s()
    {
        using var host = await MediTestHost.StartAsync();
        var foreign = new ForeignCollection();

        var error = Assert.Throws<ArgumentException>(
            () => new EdgeVectorStoreMediWriter(foreign, host.Generator, "medi"));
        Assert.Equal("collection", error.ParamName);
    }

    private static async Task<FileInfo> WriteFileAsync(MediTestHost host, string name, string content)
    {
        var directory = Path.Combine(host.Root, "docs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, content, Token).ConfigureAwait(false);
        return new FileInfo(path);
    }

    internal static async Task<IReadOnlyList<IngestedChunk>> ReadRowsAsync(IEdgeDatabase database, string documentId)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                """
                SELECT "key","source_id","document_id","ordinal","text","heading_path","char_start","char_end",
                       "token_count","page","block_kind","extractor_id","media_type","content_hash"
                FROM "chunks" WHERE "document_id" = $d ORDER BY "ordinal"
                """,
                reader => new IngestedChunk(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) || reader.GetString(5).Length == 0 ? null : reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    ContentHash.FromBlob((byte[])reader.GetValue(13)),
                    DateTimeOffset.MinValue),
                [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    /// <summary>A collection that answers no GetService request - i.e. not one of SP2's.</summary>
    private sealed class ForeignCollection : Microsoft.Extensions.VectorData.VectorStoreCollection<object, Dictionary<string, object?>>
    {
        public override string Name => "foreign";

        public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task<Dictionary<string, object?>?> GetAsync(
            object key, Microsoft.Extensions.VectorData.RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<Dictionary<string, object?>?>(null);

        public override IAsyncEnumerable<Dictionary<string, object?>> GetAsync(
            System.Linq.Expressions.Expression<Func<Dictionary<string, object?>, bool>> filter,
            int top,
            Microsoft.Extensions.VectorData.FilteredRecordRetrievalOptions<Dictionary<string, object?>>? options = null,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<Dictionary<string, object?>>();

        public override Task DeleteAsync(object key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task UpsertAsync(Dictionary<string, object?> record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task UpsertAsync(IEnumerable<Dictionary<string, object?>> records, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override IAsyncEnumerable<Microsoft.Extensions.VectorData.VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(
            TInput searchValue,
            int top,
            Microsoft.Extensions.VectorData.VectorSearchOptions<Dictionary<string, object?>>? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<Microsoft.Extensions.VectorData.VectorSearchResult<Dictionary<string, object?>>>();

        public override object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
