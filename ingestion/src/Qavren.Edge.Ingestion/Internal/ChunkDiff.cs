using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>One stored chunk, as the five columns the diff reads. Never the text.</summary>
/// <param name="Key">The stored identity.</param>
/// <param name="ContentHash">The chunk's content hash.</param>
/// <param name="Ordinal">Its stored position.</param>
/// <param name="CharStart">Its stored start offset.</param>
/// <param name="CharEnd">Its stored end offset.</param>
internal sealed record StoredChunkRow(
    string Key, ContentHash ContentHash, int Ordinal, int CharStart, int CharEnd);

/// <summary>One freshly-chunked draft with its identity assigned.</summary>
/// <param name="Draft">The chunker's output.</param>
/// <param name="Key">Spec 9.3's derived key.</param>
/// <param name="ContentHash">The hash of the EMBED text as SP3 composes it.</param>
/// <param name="DuplicateOrdinal">Prior occurrences of the same hash in the same document.</param>
internal sealed record IdentifiedChunk(
    ChunkDraft Draft, string Key, ContentHash ContentHash, int DuplicateOrdinal);

/// <summary>Spec 9.4 step 5's four sets.</summary>
/// <param name="Added">Chunks with no stored counterpart. Embedded and upserted.</param>
/// <param name="Removed">Stored rows with no fresh counterpart. Deleted, AFTER the additions.</param>
/// <param name="Unchanged">Matched by <c>(content_hash, duplicateOrdinal)</c>.</param>
/// <param name="Repaired">
/// The subset of <paramref name="Unchanged"/> whose ordinal or offsets moved. Ordinal is stored but
/// is NOT part of identity, and that is exactly what makes this cheap repair possible.
/// </param>
internal sealed record ChunkDiffResult(
    IReadOnlyList<IdentifiedChunk> Added,
    IReadOnlyList<StoredChunkRow> Removed,
    IReadOnlyList<IdentifiedChunk> Unchanged,
    IReadOnlyList<IdentifiedChunk> Repaired);

/// <summary>Spec 9.4 steps 3 to 5: chunk identity, the stored read, and the set difference.</summary>
internal static class ChunkDiff
{
    /// <summary>
    /// Spec 9.3's identity. <c>chunkContentHash</c> is over the EMBED text as SP3 composes it —
    /// breadcrumb plus text — which by spec 8.3 EXCLUDES <c>Model.DocumentPrefix</c>: the prefix is
    /// a property of the model, not of the chunk, and it lives in the recipe hash instead.
    /// </summary>
    /// <param name="sourceId">The source id.</param>
    /// <param name="documentId">The document id.</param>
    /// <param name="drafts">The chunker's output, in order.</param>
    /// <returns>The drafts with keys, hashes and duplicate ordinals assigned.</returns>
    public static IReadOnlyList<IdentifiedChunk> Identify(
        string sourceId, string documentId, IReadOnlyList<ChunkDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);

        var seen = new Dictionary<ContentHash, int>();
        var identified = new List<IdentifiedChunk>(drafts.Count);
        var source = ContentHash.OfText(sourceId);
        var document = ContentHash.OfText(documentId);

        foreach (var draft in drafts)
        {
            var contentHash = ContentHash.OfText(draft.EmbedText);
            seen.TryGetValue(contentHash, out var duplicateOrdinal);
            seen[contentHash] = duplicateOrdinal + 1;

            var key = ContentHash.Combine(
                [source, document, contentHash, new ContentHash((UInt128)(uint)duplicateOrdinal)]).ToHex();

            identified.Add(new IdentifiedChunk(draft, key, contentHash, duplicateOrdinal));
        }

        return identified;
    }

    /// <summary>
    /// Spec 9.4 step 4. One direct SQL read through <see cref="IEdgeDatabase"/>, five columns,
    /// never the text, using the <c>document_id</c> index SP2 creates for an <c>IsIndexed</c>
    /// property. No transaction is opened: this is a read.
    /// </summary>
    /// <param name="database">The database the collection lives in.</param>
    /// <param name="dataTable">The collection's data table.</param>
    /// <param name="sourceId">The source id.</param>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored rows, in ordinal order.</returns>
    public static async Task<IReadOnlyList<StoredChunkRow>> ReadStoredAsync(
        IEdgeDatabase database,
        string dataTable,
        string sourceId,
        string documentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                $"""
                 SELECT "{IngestionColumns.Key}","{IngestionColumns.ContentHash}","{IngestionColumns.Ordinal}",
                        "{IngestionColumns.CharStart}","{IngestionColumns.CharEnd}"
                 FROM "{dataTable}"
                 WHERE "{IngestionColumns.SourceId}" = $s AND "{IngestionColumns.DocumentId}" = $d
                 ORDER BY "{IngestionColumns.Ordinal}"
                 """,
                reader => new StoredChunkRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? ContentHash.Zero : ContentHash.FromBlob((byte[])reader.GetValue(1)),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)),
                [new SqliteParameter("$s", sourceId), new SqliteParameter("$d", documentId)],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Spec 9.4 step 5's set-difference on <c>(content_hash, duplicateOrdinal)</c>.</summary>
    /// <param name="fresh">The identified drafts.</param>
    /// <param name="stored">The rows currently in the collection.</param>
    /// <returns>The four sets.</returns>
    public static ChunkDiffResult Compare(
        IReadOnlyList<IdentifiedChunk> fresh, IReadOnlyList<StoredChunkRow> stored)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(stored);

        var storedByKey = new Dictionary<string, StoredChunkRow>(StringComparer.Ordinal);
        foreach (var row in stored)
        {
            storedByKey[row.Key] = row;
        }

        var added = new List<IdentifiedChunk>();
        var unchanged = new List<IdentifiedChunk>();
        var repaired = new List<IdentifiedChunk>();
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in fresh)
        {
            if (!storedByKey.TryGetValue(chunk.Key, out var row))
            {
                added.Add(chunk);
                continue;
            }

            matched.Add(chunk.Key);
            unchanged.Add(chunk);

            if (row.Ordinal != chunk.Draft.Ordinal
                || row.CharStart != chunk.Draft.CharStart
                || row.CharEnd != chunk.Draft.CharEnd)
            {
                repaired.Add(chunk);
            }
        }

        var removed = new List<StoredChunkRow>();
        foreach (var row in stored)
        {
            if (!matched.Contains(row.Key))
            {
                removed.Add(row);
            }
        }

        return new ChunkDiffResult(added, removed, unchanged, repaired);
    }
}
