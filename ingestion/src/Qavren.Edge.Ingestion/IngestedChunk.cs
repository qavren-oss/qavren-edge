using System.Globalization;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// The typed view of one stored row. SP3 writes <c>Dictionary&lt;string, object?&gt;</c> through
/// the dynamic collection; this is the mapper both directions.
/// </summary>
/// <param name="Key">Spec 9.3's 32-hex identity.</param>
/// <param name="SourceId">The source that produced it.</param>
/// <param name="DocumentId">The document it came from.</param>
/// <param name="Ordinal">Position in the document. Stored, but NOT part of identity.</param>
/// <param name="Text">The stored text — never the embed text.</param>
/// <param name="Breadcrumb">The stored breadcrumb, verbatim. Null when there is no heading path.</param>
/// <param name="CharStart">Inclusive offset into <c>ExtractedDocument.Text</c>.</param>
/// <param name="CharEnd">Exclusive offset into <c>ExtractedDocument.Text</c>.</param>
/// <param name="TokenCount">What the chunk cost.</param>
/// <param name="Page">The page, or -1 when unknown.</param>
/// <param name="BlockKind">The originating <see cref="DocumentBlockKind"/>'s name.</param>
/// <param name="ExtractorId">The extractor that produced the document.</param>
/// <param name="MediaType">The document's media type.</param>
/// <param name="ContentHash">The chunk's content hash — over the EMBED text (spec 9.3).</param>
/// <param name="CreatedUtc">When the row was written.</param>
public sealed record IngestedChunk(
    string Key,
    string SourceId,
    string DocumentId,
    int Ordinal,
    string Text,
    string? Breadcrumb,
    int CharStart,
    int CharEnd,
    int TokenCount,
    int Page,
    string BlockKind,
    string ExtractorId,
    string MediaType,
    ContentHash ContentHash,
    DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// <see cref="Breadcrumb"/> split on <see cref="IngestionColumns.HeadingPathSeparator"/>.
    /// Empty when there is none. This is the ONLY place the structural array comes back; there is
    /// no array column, because SP2's <c>SqliteTypeMap</c> has no array type but <c>byte[]</c>.
    /// </summary>
    public IReadOnlyList<string> HeadingPath => IngestionColumns.SplitBreadcrumb(Breadcrumb);

    /// <summary>Reads one dynamic record back into the typed view.</summary>
    /// <param name="record">The record the dynamic collection handed back.</param>
    /// <returns>The typed chunk.</returns>
    public static IngestedChunk FromRecord(IReadOnlyDictionary<string, object?> record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new IngestedChunk(
            Str(record, IngestionColumns.Key) ?? string.Empty,
            Str(record, IngestionColumns.SourceId) ?? string.Empty,
            Str(record, IngestionColumns.DocumentId) ?? string.Empty,
            Number(record, IngestionColumns.Ordinal),
            Str(record, IngestionColumns.Text) ?? string.Empty,
            NullIfEmpty(Str(record, IngestionColumns.HeadingPath)),
            Number(record, IngestionColumns.CharStart),
            Number(record, IngestionColumns.CharEnd),
            Number(record, IngestionColumns.TokenCount),
            Number(record, IngestionColumns.Page),
            Str(record, IngestionColumns.BlockKind) ?? nameof(DocumentBlockKind.Paragraph),
            Str(record, IngestionColumns.ExtractorId) ?? string.Empty,
            Str(record, IngestionColumns.MediaType) ?? string.Empty,
            record.TryGetValue(IngestionColumns.ContentHash, out var hash) && hash is byte[] blob
                ? Ingestion.ContentHash.FromBlob(blob)
                : Ingestion.ContentHash.Zero,
            record.TryGetValue(IngestionColumns.CreatedUtc, out var created) && created is DateTimeOffset stamp
                ? stamp
                : default);
    }

    /// <summary>Renders this chunk plus its vector as the dynamic record the collection upserts.</summary>
    /// <param name="embedding">The vector SP3 resolved in spec 9.5 step a1.</param>
    /// <returns>The record.</returns>
    public IDictionary<string, object?> ToRecord(ReadOnlyMemory<float> embedding) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [IngestionColumns.Key] = Key,
            [IngestionColumns.Embedding] = embedding,
            [IngestionColumns.SourceId] = SourceId,
            [IngestionColumns.DocumentId] = DocumentId,
            [IngestionColumns.Ordinal] = Ordinal,
            [IngestionColumns.Text] = Text,
            [IngestionColumns.HeadingPath] = Breadcrumb ?? string.Empty,
            [IngestionColumns.ContentHash] = ContentHash.ToBlob(),
            [IngestionColumns.CharStart] = CharStart,
            [IngestionColumns.CharEnd] = CharEnd,
            [IngestionColumns.TokenCount] = TokenCount,
            [IngestionColumns.Page] = Page,
            [IngestionColumns.BlockKind] = BlockKind,
            [IngestionColumns.ExtractorId] = ExtractorId,
            [IngestionColumns.MediaType] = MediaType,
            [IngestionColumns.CreatedUtc] = CreatedUtc,
        };

    private static string? Str(IReadOnlyDictionary<string, object?> record, string column) =>
        record.TryGetValue(column, out var value) ? value as string : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static int Number(IReadOnlyDictionary<string, object?> record, string column) =>
        record.TryGetValue(column, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;
}
