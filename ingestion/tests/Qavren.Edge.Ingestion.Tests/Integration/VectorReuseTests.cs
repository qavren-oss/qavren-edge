using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// A document whose bytes another fully indexed document in the same collection already holds,
/// under the same recipe, takes that document's vectors instead of calling the generator: a rename,
/// a move, a duplicate file. The generator here seeds every vector from the WHOLE chunk text, so a
/// vector copied against the wrong chunk shows up as a mismatch rather than hiding behind twenty
/// identical vectors.
/// </summary>
public sealed class VectorReuseTests
{
    /// <summary>One chunk per paragraph, as in <see cref="IncrementalIndexTests"/>.</summary>
    private const int Paragraphs = 20;
    private const int WordsPerParagraph = 150;

    private static readonly string Text = Prose.Document(Paragraphs, WordsPerParagraph);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_renamed_file_is_not_re_embedded_its_chunks_carry_the_donors_vectors_and_the_old_id_is_pruned()
    {
        using var host = await IntegrationHost.StartAsync(Options());
        var source = new RecordingSource().Add("old.txt", Text);

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(Paragraphs, first.ChunksAdded);
        var donor = await VectorsAsync(host.Database, "old.txt");
        var calls = host.Generator.CallCount;

        source.Remove("old.txt").Add("new.txt", Text);
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
        Assert.Equal(calls, host.Generator.CallCount);
        Assert.Equal(0, second.EmbedCalls);
        Assert.Equal(0, second.TokensEmbedded);

        // Counted as the indexed document it is: its chunks were upserted, nothing was embedded.
        var renamed = Assert.Single(second.Documents);
        Assert.Equal("new.txt", renamed.DocumentId);
        Assert.Equal(IngestionDocumentOutcome.Indexed, renamed.Outcome);
        Assert.Equal(Paragraphs, renamed.ChunksAdded);
        Assert.Equal(0, renamed.TokensEmbedded);
        Assert.Equal(1, second.DocumentsIndexed);
        Assert.Equal(Paragraphs, second.ChunksAdded);
        Assert.Contains(
            host.Logs.Lines,
            l => l.StartsWith("905|Document 'new.txt': 20 chunk vectors copied from document 'old.txt'", StringComparison.Ordinal));

        // Read before the prune: the old id is gone at the end of the run, and its vectors live on,
        // chunk for chunk, under the new one.
        Assert.Equal(1, second.DocumentsRemoved);
        Assert.Empty(await Db.KeysAsync(host.Database, "old.txt"));
        Assert.Equal(donor, await VectorsAsync(host.Database, "new.txt"));
        Assert.Equal(IngestionDocumentStatus.Indexed, await Db.DocumentColumnAsync(host.Database, "new.txt", "status"));
        Assert.Equal(
            Paragraphs.ToString(CultureInfo.InvariantCulture),
            await Db.DocumentColumnAsync(host.Database, "new.txt", "chunk_count"));

        // The FTS5 sidecar followed the copied rows through the data table's insert trigger.
        await Db.FtsIntegrityCheckAsync(host.Database);
        Assert.Equal(1, await Db.FtsMatchCountAsync(host.Database, "p19w3"));
        Assert.Equal(Paragraphs, await Db.CountAsync(host.Database, Db.VectorTable));
    }

    /// <summary>
    /// Reuse stores exactly what re-embedding would have stored - every column of every row, the
    /// vector bit for bit - which is why it needs no recipe or extractor version bump. The
    /// comparison is against a fresh database that embedded the renamed file from scratch with the
    /// same deterministic generator.
    /// </summary>
    [Fact]
    public async Task A_reused_document_stores_the_same_records_re_embedding_would_have()
    {
        using var host = await IntegrationHost.StartAsync(Options());
        var source = new RecordingSource().Add("old.txt", Text);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        source.Remove("old.txt").Add("new.txt", Text);
        var calls = host.Generator.CallCount;
        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(calls, host.Generator.CallCount);

        using var fresh = await IntegrationHost.StartAsync(Options());
        await fresh.Pipeline.RunAsync(new RecordingSource().Add("new.txt", Text), cancellationToken: Token);
        Assert.NotEqual(0, fresh.Generator.CallCount);

        Assert.Equal(await RecordsAsync(fresh.Database, "new.txt"), await RecordsAsync(host.Database, "new.txt"));
    }

    [Fact]
    public async Task A_duplicate_in_the_same_run_is_embedded_once()
    {
        using var host = await IntegrationHost.StartAsync(Options());
        var source = new RecordingSource().Add("a.txt", Text).Add("copy of a.txt", Text);

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(2, result.DocumentsIndexed);
        Assert.Equal(2 * Paragraphs, result.ChunksAdded);
        Assert.Equal(Paragraphs, host.Generator.Calls.Sum(call => call.Count));
        Assert.Equal(await VectorsAsync(host.Database, "a.txt"), await VectorsAsync(host.Database, "copy of a.txt"));
    }

    [Fact]
    public async Task A_duplicate_added_in_a_later_run_is_not_embedded_and_leaves_the_original_alone()
    {
        using var host = await IntegrationHost.StartAsync(Options());
        var source = new RecordingSource().Add("a.txt", Text);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var original = await RecordsAsync(host.Database, "a.txt");
        var calls = host.Generator.CallCount;

        source.Add("b.txt", Text);
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(calls, host.Generator.CallCount);
        Assert.Equal(1, second.DocumentsSkipped);
        Assert.Equal(1, second.DocumentsIndexed);
        Assert.Equal(0, second.DocumentsRemoved);
        Assert.Equal(original, await RecordsAsync(host.Database, "a.txt"));
        Assert.Equal(await VectorsAsync(host.Database, "a.txt"), await VectorsAsync(host.Database, "b.txt"));
    }

    /// <summary>
    /// The donor must have been indexed under the CURRENT recipe. A rename across a recipe change
    /// re-embeds everything, exactly as the recipe change alone would have.
    /// </summary>
    [Fact]
    public async Task A_recipe_change_disables_reuse_and_the_renamed_file_is_re_embedded()
    {
        var root = IntegrationHost.NewRoot();
        try
        {
            using (var first = await IntegrationHost.StartAsync(Options(root)))
            {
                await first.Pipeline.RunAsync(new RecordingSource().Add("old.txt", Text), cancellationToken: Token);
            }

            using var second = await IntegrationHost.StartAsync(Options(root, o => o.Chunking.MaxTokens = 200));
            var result = await second.Pipeline.RunAsync(
                new RecordingSource().Add("new.txt", Text), cancellationToken: Token);

            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
            Assert.Equal(1, result.DocumentsRemoved);
            Assert.Equal(Paragraphs, second.Generator.Calls.Sum(call => call.Count));
            Assert.Equal(result.ChunksAdded, second.Generator.Calls.Sum(call => call.Count));
        }
        finally
        {
            IntegrationHost.DeleteRoot(root);
        }
    }

    /// <summary>
    /// Only an <c>Indexed</c> donor counts. A row that says <c>Failed</c> over the same bytes and
    /// recipe - its chunks still in the table - is not evidence those chunks are whole.
    /// </summary>
    [Fact]
    public async Task A_donor_whose_status_is_not_Indexed_is_not_used()
    {
        using var host = await IntegrationHost.StartAsync(Options());
        var source = new RecordingSource().Add("a.txt", Text);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(Paragraphs, (await Db.KeysAsync(host.Database, "a.txt")).Count);

        await ExecuteAsync(
            host.Database,
            $"""UPDATE "{Db.DocumentTable}" SET "status" = '{IngestionDocumentStatus.Failed}' WHERE "document_id" = 'a.txt'""");
        var embedded = host.Generator.Calls.Sum(call => call.Count);

        source.Add("b.txt", Text);
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, second.DocumentsIndexed);
        Assert.Equal(Paragraphs, host.Generator.Calls.Sum(call => call.Count) - embedded);
    }

    /// <summary>
    /// The copied records carry the NEW document's identity and per-document columns - source id,
    /// document id, key, media type, created time - and only the per-chunk ones come from the
    /// donor. The copy here crosses sources and media types (<c>text/plain</c> to
    /// <c>text/csv</c>, the same plain-text extractor and so the same recipe).
    /// </summary>
    [Fact]
    public async Task The_copied_records_carry_the_new_documents_per_document_fields_not_the_donors()
    {
        var clock = TestClock.Create();
        var options = Options();
        options.Time = clock;
        using var host = await IntegrationHost.StartAsync(options);
        await host.Pipeline.RunAsync(new RecordingSource("phone").Add("notes/a.txt", Text), cancellationToken: Token);
        var donor = await RecordsAsync(host.Database, "notes/a.txt");
        var calls = host.Generator.CallCount;

        clock.Advance(TimeSpan.FromHours(1));
        var copy = new RecordingSource("sdcard").Add("backup/a.csv", Text, "text/csv");
        var result = await host.Pipeline.RunAsync(copy, cancellationToken: Token);

        Assert.Equal(calls, host.Generator.CallCount);
        Assert.Equal(1, result.DocumentsIndexed);

        var rows = await ColumnsAsync(host.Database, "backup/a.csv");
        var donorRows = await ColumnsAsync(host.Database, "notes/a.txt");
        Assert.Equal(Paragraphs, rows.Count);
        Assert.All(rows, r => Assert.Equal("sdcard", r.SourceId));
        Assert.All(rows, r => Assert.Equal("backup/a.csv", r.DocumentId));
        Assert.All(rows, r => Assert.Equal("text/csv", r.MediaType));
        Assert.All(donorRows, r => Assert.Equal("text/plain", r.MediaType));
        Assert.Empty(rows.Select(r => r.Key).Intersect(donorRows.Select(r => r.Key), StringComparer.Ordinal));
        Assert.Single(rows.Select(r => r.CreatedUtc).Distinct(StringComparer.Ordinal));
        Assert.Single(donorRows.Select(r => r.CreatedUtc).Distinct(StringComparer.Ordinal));
        Assert.NotEqual(donorRows[0].CreatedUtc, rows[0].CreatedUtc);

        // The donor is untouched, and the copy is what a fresh database embedding it would hold.
        Assert.Equal(donor, await RecordsAsync(host.Database, "notes/a.txt"));
        using var fresh = await IntegrationHost.StartAsync(Options());
        await fresh.Pipeline.RunAsync(new RecordingSource("sdcard").Add("backup/a.csv", Text, "text/csv"), cancellationToken: Token);
        Assert.Equal(await RecordsAsync(fresh.Database, "backup/a.csv"), await RecordsAsync(host.Database, "backup/a.csv"));
        Assert.Equal(await VectorsAsync(host.Database, "notes/a.txt"), await VectorsAsync(host.Database, "backup/a.csv"));
    }

    /// <summary>
    /// A donor whose rows no longer agree with its state row - here one chunk is missing - is not
    /// used, and the document is embedded normally rather than failed.
    /// </summary>
    [Fact]
    public async Task A_donor_whose_rows_disagree_with_its_state_row_falls_back_to_embedding()
    {
        using var host = await IntegrationHost.StartAsync(Options());
        var source = new RecordingSource().Add("a.txt", Text);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        await ExecuteAsync(host.Database, $"""DELETE FROM "{Db.DataTable}" WHERE "document_id" = 'a.txt' AND "ordinal" = 7""");
        var embedded = host.Generator.Calls.Sum(call => call.Count);

        source.Add("b.txt", Text);
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
        Assert.Equal(1, second.DocumentsIndexed);
        Assert.Equal(0, second.DocumentsFailed);
        Assert.Equal(Paragraphs, host.Generator.Calls.Sum(call => call.Count) - embedded);
        Assert.Equal(Paragraphs, (await VectorsAsync(host.Database, "b.txt")).Count);
    }

    /// <summary>
    /// A donor chunk whose vector cannot be read costs only its own write window: that window is
    /// embedded, every other window is still copied.
    /// </summary>
    [Fact]
    public async Task A_donor_with_a_missing_vector_embeds_only_the_window_that_needed_it()
    {
        using var host = await IntegrationHost.StartAsync(Options(ingestion: o => o.WriteBatchSize = 5));
        var source = new RecordingSource().Add("a.txt", Text);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var donor = await VectorsAsync(host.Database, "a.txt");

        await ExecuteAsync(
            host.Database,
            $"""
             DELETE FROM "{Db.VectorTable}" WHERE rowid =
               (SELECT "_rowid" FROM "{Db.DataTable}" WHERE "document_id" = 'a.txt' AND "ordinal" = 7)
             """);
        var calls = host.Generator.CallCount;

        source.Add("b.txt", Text);
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
        Assert.Equal(0, second.DocumentsFailed);
        var call = Assert.Single(host.Generator.Calls.Skip(calls));
        Assert.Equal(5, call.Count);
        Assert.Equal(1, second.EmbedCalls);
        Assert.Equal(donor, await VectorsAsync(host.Database, "b.txt"));
    }

    private static IntegrationHostOptions Options(string? root = null, Action<IngestionOptions>? ingestion = null) =>
        new()
        {
            Root = root,
            Ingestion = ingestion,
            Generator = new RecordingEmbeddingGenerator(distinctPerText: true),
        };

    /// <summary>One document's chunks in ordinal order: the per-chunk identity plus the vector blob.</summary>
    private static async Task<IReadOnlyList<string>> VectorsAsync(IEdgeDatabase database, string documentId)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                $"""
                 SELECT d."ordinal", hex(d."content_hash"), d."char_start", d."char_end", hex(v."embedding")
                 FROM "{Db.DataTable}" d JOIN "{Db.VectorTable}" v ON v.rowid = d."_rowid"
                 WHERE d."document_id" = $d
                 ORDER BY d."ordinal"
                 """,
                r => string.Join(
                    '|',
                    r.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                    r.GetString(1),
                    r.GetInt32(2).ToString(CultureInfo.InvariantCulture),
                    r.GetInt32(3).ToString(CultureInfo.InvariantCulture),
                    r.GetString(4)),
                [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    /// <summary>Every stored column of one document's rows, the vector included, in key order.</summary>
    private static async Task<IReadOnlyList<string>> RecordsAsync(IEdgeDatabase database, string documentId)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                $"""
                 SELECT d."key", d."source_id", d."document_id", d."ordinal", d."text", d."heading_path",
                        hex(d."content_hash"), d."char_start", d."char_end", d."token_count", d."page",
                        d."block_kind", d."extractor_id", d."media_type", hex(v."embedding")
                 FROM "{Db.DataTable}" d
                 JOIN "{Db.VectorTable}" v ON v.rowid = d."_rowid"
                 WHERE d."document_id" = $d
                 ORDER BY d."key"
                 """,
                r =>
                {
                    var builder = new StringBuilder();
                    for (var i = 0; i < r.FieldCount; i++)
                    {
                        builder.Append(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('\u001f');
                    }

                    return builder.ToString();
                },
                [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<RowIdentity>> ColumnsAsync(IEdgeDatabase database, string documentId)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                $"""
                 SELECT "key", "source_id", "document_id", "media_type", CAST("created_utc" AS TEXT)
                 FROM "{Db.DataTable}" WHERE "document_id" = $d ORDER BY "ordinal"
                 """,
                r => new RowIdentity(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)),
                [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(IEdgeDatabase database, string sql)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(sql, cancellationToken: Token).ConfigureAwait(false);
        }
    }

    private sealed record RowIdentity(string Key, string SourceId, string DocumentId, string MediaType, string CreatedUtc);
}
