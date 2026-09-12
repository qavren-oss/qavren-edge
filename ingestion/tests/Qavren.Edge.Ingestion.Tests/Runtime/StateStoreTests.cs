using Microsoft.Data.Sqlite;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// The three state tables: the round trip, the four 62xx faults, the run-history trim, and the
/// rule that a <c>Failed</c> row is KEPT so the next run skips it rather than retrying a broken
/// file forever — while a recipe bump retries it automatically.
/// </summary>
public sealed class StateStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_row_round_trips()
    {
        using var host = await IngestionTestHost.StartAsync();
        var store = host.State();
        var hash = ContentHash.OfText("body");
        var recipe = ContentHash.OfText("recipe");
        var stamp = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        var row = new IngestionDocumentState(
            "src", "a.txt", hash, recipe, 42, stamp, "text", 3,
            IngestionDocumentStatus.Indexed, null, "run-1", stamp);

        await store.UpsertDocumentAsync("chunks", row, Token);
        var read = await store.GetDocumentAsync("chunks", "src", "a.txt", Token);

        Assert.NotNull(read);
        Assert.Equal(hash, read.ContentHash);
        Assert.Equal(recipe, read.RecipeHash);
        Assert.Equal(42, read.SizeBytes);
        Assert.Equal(stamp, read.ModifiedUtc);
        Assert.Equal("text", read.ExtractorId);
        Assert.Equal(3, read.ChunkCount);
        Assert.Equal(IngestionDocumentStatus.Indexed, read.Status);
        Assert.Equal("run-1", read.LastRunId);
    }

    [Fact]
    public async Task Absent_tables_are_6201_with_the_AddIngestion_remediation()
    {
        using var host = await IngestionTestHost.StartAsync();
        var store = host.State("qedge_absent");

        var error = await Assert.ThrowsAsync<EdgeIngestionStateException>(() => store.VerifyAsync(Token));

        Assert.Equal(EdgeErrorCode.IngestionStateMissing, error.Code);
        Assert.Contains("AddIngestion", error.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_hash_algorithm_is_6202_rather_than_a_mis_diff()
    {
        using var host = await IngestionTestHost.StartAsync();
        await SetMetaAsync(host, IngestionSchema.MetaHashAlgorithmKey, "sha256-v1");

        var error = await Assert.ThrowsAsync<EdgeIngestionStateException>(() => host.State().VerifyAsync(Token));

        Assert.Equal(EdgeErrorCode.IngestionHashAlgorithmMismatch, error.Code);
    }

    [Fact]
    public async Task An_unknown_schema_version_is_6203()
    {
        using var host = await IngestionTestHost.StartAsync();
        await SetMetaAsync(host, IngestionSchema.MetaSchemaVersionKey, "99");

        var error = await Assert.ThrowsAsync<EdgeIngestionStateException>(() => host.State().VerifyAsync(Token));

        Assert.Equal(EdgeErrorCode.IngestionStateSchemaUnsupported, error.Code);
        Assert.Equal(IngestionSchema.StateSchemaVersion, error.ExpectedSchemaVersion);
        Assert.Equal(99, error.ActualSchemaVersion);
    }

    [Fact]
    public void A_hash_blob_of_the_wrong_length_is_6204()
    {
        var error = Assert.Throws<EdgeIngestionStateException>(() => ContentHash.FromBlob(new byte[15]));

        Assert.Equal(EdgeErrorCode.IngestionStateCorrupt, error.Code);
    }

    [Fact]
    public async Task The_run_table_is_trimmed_to_RunHistoryLimit()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.RunHistoryLimit = 3);
        var source = new RecordingSource().Add("a.txt", "alpha");

        for (var i = 0; i < 6; i++)
        {
            source.Replace("a.txt", "alpha " + i);
            await host.Pipeline.RunAsync(source, cancellationToken: Token);
        }

        Assert.Equal(3, await host.State().RunCountAsync("chunks", Token));
    }

    [Fact]
    public async Task A_failed_row_survives_a_re_run_on_an_unchanged_hash_and_a_recipe_bump_retries_it()
    {
        var fault = new EdgeExtractionException(EdgeErrorCode.DocumentMalformed, "bad") { ExtractorName = "throwing" };
        using var host = await IngestionTestHost.StartAsync(o => o.Extractors.Add(new ThrowingExtractor(fault)));
        var source = new RecordingSource().Add("a.txt", "alpha");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var row = await host.State().GetDocumentAsync("chunks", "memory", "a.txt", Token);
        Assert.NotNull(row);
        Assert.Equal(IngestionDocumentStatus.Failed, row.Status);

        // Second run, unchanged content: the row is KEPT and the extractor is NOT retried.
        source.ResetOpens();
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(1, second.DocumentsSkipped);
        Assert.Equal(1, source.Opens["a.txt"]);

        // A recipe bump - here a chunker change - dirties the row, so the retry is automatic.
        using var bumped = await IngestionTestHost.StartAsync(o => o.Extractors.Add(new ThrowingExtractor(fault)));
        var third = await bumped.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(1, third.DocumentsFailed);
    }

    private static async Task SetMetaAsync(IngestionTestHost host, string key, string value)
    {
        var connection = await host.Database.OpenConnectionAsync(Token).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            await connection.ExecuteAsync(
                """UPDATE "qedge_ingest_meta" SET "value" = $v WHERE "key" = $k""",
                [new SqliteParameter("$v", value), new SqliteParameter("$k", key)],
                Token).ConfigureAwait(true);
        }
    }
}
