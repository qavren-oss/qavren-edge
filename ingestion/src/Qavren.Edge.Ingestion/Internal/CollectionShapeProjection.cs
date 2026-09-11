using Qavren.Edge.VectorData;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Projects the per-collection overrides onto an <see cref="EdgeVectorStoreOptions"/>, which is
/// what <c>EdgeVectorSchema</c>'s constructor takes. A five-line reimplementation of SP2's internal
/// <c>EdgeVectorCollectionSchemaFactory.ToStoreOptions</c>, because that type is not visible here
/// and <c>Qavren.Edge.VectorData</c> grants no <c>InternalsVisibleTo</c>.
/// <para>
/// <c>FullTextRemoveDiacritics</c> does NOT come from the collection options: SP2 declares
/// <c>EdgeVectorStoreCollectionOptions.RemoveDiacritics</c> as <c>internal</c>, so nothing outside
/// that assembly can set or read it. It comes from <see cref="IngestionOptions"/> instead (plan
/// adjustment 4), and error 6011's remediation names that member.
/// </para>
/// </summary>
internal static class CollectionShapeProjection
{
    public static EdgeVectorStoreOptions ToStoreOptions(
        EdgeVectorStoreCollectionOptions options, int fullTextRemoveDiacritics)
    {
        ArgumentNullException.ThrowIfNull(options);

        var store = new EdgeVectorStoreOptions();

        if (options.VectorTableName is { Length: > 0 } vectorTable)
        {
            store.VectorTableNameFormat = Escape(vectorTable);
        }

        if (options.FullTextTableName is { Length: > 0 } fullTextTable)
        {
            store.FullTextTableNameFormat = Escape(fullTextTable);
        }

        if (options.ChunkSize is { } chunkSize)
        {
            store.ChunkSize = chunkSize;
        }

        if (options.FullTextTokenizer is { } tokenizer)
        {
            store.FullTextTokenizer = tokenizer;
        }

        store.FullTextRemoveDiacritics = fullTextRemoveDiacritics;
        return store;

        // The *NameFormat properties are COMPOSITE FORMAT STRINGS, so a table name containing '{'
        // that is passed through unescaped is a FormatException at DDL time.
        static string Escape(string literal) => literal
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
    }
}
