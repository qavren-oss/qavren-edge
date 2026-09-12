using Qavren.Edge.Ingestion;
using Qavren.Edge.Sample.Models;
using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Sample.Pages;

/// <summary>
/// Spec 18: insert notes, then run vector, keyword and hybrid search side by side with all three
/// scores visible, so the distance-versus-RRF polarity inversion (spec 13.3) is something a reader
/// SEES rather than reads about. There is no dedicated keyword-only search method on
/// <see cref="EdgeVectorStoreCollection{TKey, TRecord}"/> (spec 13.3 - MEVD ships only vector search
/// and hybrid search over SQLite), so the keyword column runs the same
/// <c>HybridSearchAsync</c> RRF fusion with <see cref="EdgeHybridSearchOptions{TRecord}.VectorWeight"/>
/// forced to zero, which removes the vector term from the fused score entirely and leaves a pure
/// FTS5-ranked RRF score - a real, if unlabelled-by-the-library, keyword-only lane built from public
/// API rather than a raw SQL escape hatch.
/// </summary>
public partial class SearchPage : ContentPage
{
    private const int Top = 5;

    private readonly EdgeVectorStore _store;

    public SearchPage(EdgeVectorStore store)
    {
        InitializeComponent();
        _store = store;
    }

    private static readonly (string Title, string Body)[] SeedNotes =
    [
        ("Roof leak", "A roof leak after the storm left water damage across the attic insulation."),
        ("Furnace tune-up", "Annual furnace tune-up and filter replacement before winter."),
        ("Garage door sensor", "The garage door sensor stopped responding and needs a new battery."),
        ("Basement flooding", "Heavy rain caused minor basement flooding near the sump pump."),
        ("Fence repair", "Two fence panels came loose in the wind and need new brackets."),
    ];

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Sample app; not published trimmed. See EdgeVectorStore.GetCollection's own docs.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Sample app; not published trimmed. See EdgeVectorStore.GetCollection's own docs.")]
    private EdgeVectorStoreCollection<string, Note> Collection =>
        _store.GetCollection<string, Note>(MauiProgram.NotesCollectionName);

    /// <summary>
    /// SP3's chunk collection (plan Task 8.2), read back the way SP3 itself reads it: the dynamic
    /// collection over the same definition <c>AddIngestion</c> registered, with
    /// <see cref="IngestedChunk.FromRecord"/> as the typed view. No trim annotations needed - the
    /// dynamic path reflects over nothing.
    /// </summary>
    private EdgeDynamicVectorStoreCollection Chunks =>
        _store.GetDynamicCollection(MauiProgram.ChunksCollectionName, MauiProgram.ChunkCollectionDefinition());

    private async void OnSeed(object? sender, EventArgs e)
    {
        StatusLabel.Text = "Seeding...";

        foreach (var (title, body) in SeedNotes)
        {
            await Collection.UpsertAsync(new Note
            {
                Key = title,
                Title = title,
                Body = body,
            });
        }

        StatusLabel.Text = $"Seeded {SeedNotes.Length} notes.";
    }

    private async void OnSearch(object? sender, EventArgs e)
    {
        var query = string.IsNullOrWhiteSpace(QueryEntry.Text) ? "water damage" : QueryEntry.Text;
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        StatusLabel.Text = "Searching...";

        var collection = Collection;

        var rows = new Dictionary<string, ResultRow>(StringComparer.Ordinal);

        static ResultRow RowFor(Dictionary<string, ResultRow> rows, MEVD.VectorSearchResult<Note> hit)
        {
            if (!rows.TryGetValue(hit.Record.Key, out var row))
            {
                row = new ResultRow { Title = hit.Record.Title };
                rows[hit.Record.Key] = row;
            }

            return row;
        }

        await foreach (var hit in collection.SearchAsync(query, Top))
        {
            RowFor(rows, hit).VectorDistance = hit.Score;
        }

        // Note has TWO full-text columns (Title and Body). MEVD refuses a hybrid search that names
        // neither - "multiple text data properties that have full text indexing enabled" - and this
        // handler is async void, so the unnamed call crashed the whole process (measured on the
        // Windows head 2026-09-11, plan Task 8.2's hand run). Body is the column the seed notes'
        // prose lives in, so the keyword lane matches on it.
        var keywordOnly = new EdgeHybridSearchOptions<Note> { VectorWeight = 0, AdditionalProperty = n => n.Body };
        await foreach (var hit in collection.HybridSearchAsync(query, keywords, Top, keywordOnly))
        {
            RowFor(rows, hit).KeywordScore = hit.Score;
        }

        var hybrid = new EdgeHybridSearchOptions<Note> { AdditionalProperty = n => n.Body };
        await foreach (var hit in collection.HybridSearchAsync(query, keywords, Top, hybrid))
        {
            RowFor(rows, hit).HybridScore = hit.Score;
        }

        Results.ItemsSource = rows.Values
            .OrderByDescending(r => r.HybridScore ?? double.NegativeInfinity)
            .ToArray();

        StatusLabel.Text = $"{rows.Count} distinct note(s) across the three lanes.";

        // The Ingest page's chunks. Same query string; the store's generator embeds it exactly as
        // SP3's ChunkWriter embedded the chunks (MiniLM carries no prefixes).
        ChunkStatusLabel.Text = "Searching chunks...";
        var chunkRows = new List<ChunkRow>();
        try
        {
            await foreach (var hit in Chunks.SearchAsync(query, Top))
            {
                var chunk = IngestedChunk.FromRecord(hit.Record);
                chunkRows.Add(new ChunkRow
                {
                    Score = hit.Score,
                    Where = $"{chunk.DocumentId} #{chunk.Ordinal}" + (chunk.Breadcrumb is { } crumb ? "  " + crumb : string.Empty),
                    Snippet = chunk.Text,
                });
            }

            ChunkResults.ItemsSource = chunkRows;
            ChunkStatusLabel.Text = chunkRows.Count == 0
                ? "No chunks. Ingest something on the Ingest page first."
                : $"{chunkRows.Count} chunk(s) from {MauiProgram.ChunksCollectionName}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ChunkStatusLabel.Text = "Chunk search threw: " + ex.Message;
        }
    }

    private sealed class ChunkRow
    {
        public double? Score { get; init; }

        public string Where { get; init; } = "";

        public string Snippet { get; init; } = "";

        public string ScoreText => Score is { } d ? d.ToString("F4") : "-";
    }

    private sealed class ResultRow
    {
        public string Title { get; init; } = "";

        public double? VectorDistance { get; set; }

        public double? KeywordScore { get; set; }

        public double? HybridScore { get; set; }

        public string VectorDistanceText => VectorDistance is { } d ? d.ToString("F4") : "-";

        public string KeywordScoreText => KeywordScore is { } s ? s.ToString("F4") : "-";

        public string HybridScoreText => HybridScore is { } s ? s.ToString("F4") : "-";
    }
}
