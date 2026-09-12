using Microsoft.Data.Sqlite;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 9.5's durability story, each claim given a mechanism: a crash after the second write
/// window, <c>SQLITE_BUSY</c> under a concurrent writer, the transaction invariant at integration
/// scope, and a cancelled run whose resume lands byte-identical to an uninterrupted one. Task 7.1
/// Step 3.
/// </summary>
public sealed class DurabilityTests
{
    /// <summary>100 paragraphs of 150 whitespace tokens: one chunk each, four write windows of 32.</summary>
    private const int Paragraphs = 100;
    private const int WordsPerParagraph = 150;
    private const int WindowSize = 32;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The load-bearing claim of spec 9.5. Windows one and two commit, window three's upsert
    /// throws - the process "died" between the embed and the write - and the state row is never
    /// reached, so the document stays dirty. The re-run redoes only its own remainder: the 64
    /// committed chunks are found by hash, the 36 un-written ones are the only texts that reach the
    /// generator, and the key set comes out with no duplicates.
    /// </summary>
    [Fact]
    public async Task A_crash_after_the_second_write_window_resumes_with_only_the_unwritten_chunks_embedded()
    {
        FaultingEdgeDatabase? faulting = null;
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            WrapDatabase = inner => faulting = new FaultingEdgeDatabase(inner) { FailOnArmedTransaction = 1 },
        });
        Assert.NotNull(faulting);

        var embedCalls = 0;
        host.Generator.OnCall = () =>
        {
            // The third embed call is window three; the FIRST transaction after it is its upsert.
            if (++embedCalls == 3)
            {
                faulting.Arm();
            }
        };

        var source = new RecordingSource().Add("long.txt", Prose.Document(Paragraphs, WordsPerParagraph));

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, first.Outcome);
        Assert.Equal(1, faulting.Faults);
        var failed = Assert.Single(first.Failures);
        Assert.Equal(EdgeErrorCode.IngestionWriteFailed, failed.Failure!.Code);

        // Two windows landed; the torn document has NO state row, so it is still dirty.
        Assert.Equal(2 * WindowSize, await Db.CountAsync(host.Database, Db.DataTable));
        Assert.Null(await Db.DocumentContentHashAsync(host.Database, "long.txt"));

        var storedBeforeResume = (await Db.ChunksAsync(host.Database, "long.txt")).Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
        var callsBeforeResume = host.Generator.CallCount;
        host.Generator.OnCall = null;

        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
        Assert.Empty(second.Failures);
        Assert.Equal(1, second.DocumentsIndexed);
        Assert.Equal(2 * WindowSize, second.ChunksUnchanged);
        Assert.Equal(Paragraphs - (2 * WindowSize), second.ChunksAdded);
        Assert.Equal(0, second.ChunksRemoved);

        // Only the un-written chunks were embedded: every text the generator saw on the resume is
        // one that was NOT stored when the resume began, and together they are exactly the rest.
        var embeddedOnResume = host.Generator.Calls.Skip(callsBeforeResume).SelectMany(c => c).ToList();
        Assert.Equal(Paragraphs - (2 * WindowSize), embeddedOnResume.Count);
        Assert.All(embeddedOnResume, text => Assert.DoesNotContain(text, storedBeforeResume));

        var keys = await Db.KeysAsync(host.Database, "long.txt");
        Assert.Equal(Paragraphs, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Paragraphs, await Db.CountAsync(host.Database, Db.DataTable));
        Assert.Equal(Paragraphs, await Db.CountAsync(host.Database, Db.VectorTable));
        Assert.NotNull(await Db.DocumentContentHashAsync(host.Database, "long.txt"));
    }

    /// <summary>
    /// A concurrent writer holds the write lock across the state-row transaction. That is spec 9.5
    /// step d, so the failure is 6205, the run is <c>Failed</c>, and the document's stored hash is
    /// still the OLD one - the chunks for the new content landed (additions first), but nothing
    /// claims they are complete until the next run finds them by hash and writes the row.
    /// </summary>
    [Fact]
    public async Task SQLITE_BUSY_on_the_state_row_is_6205_and_the_document_stays_at_its_old_hash()
    {
        FaultingEdgeDatabase? faulting = null;
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            // Short, so SQLite's own busy handler gives up quickly; the wrapper caps MDS's spin.
            Sqlite = o => o.BusyTimeout = TimeSpan.FromMilliseconds(100),
            WrapDatabase = inner => faulting = new FaultingEdgeDatabase(inner),
        });
        Assert.NotNull(faulting);

        // One paragraph of 150 tokens is one chunk; appending a second keeps the first chunk's key
        // AND ordinal, so the edited run has one added chunk, no removal and no repair - after the
        // embed call the transactions are exactly [upsert, state row].
        var source = new RecordingSource().Add("a.txt", Prose.Paragraph(0, WordsPerParagraph));
        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, first.Outcome);
        var oldHash = await Db.DocumentContentHashAsync(host.Database, "a.txt");
        Assert.NotNull(oldHash);

        faulting.LockOnArmedTransaction = 2;
        host.Generator.OnCall = faulting.Arm;
        source.Replace("a.txt", Prose.Paragraph(0, WordsPerParagraph) + "\n\n" + Prose.Paragraph(1, WordsPerParagraph));

        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Failed, second.Outcome);
        Assert.NotNull(second.Failure);
        Assert.Equal(EdgeErrorCode.IngestionCheckpointWriteFailed, second.Failure.Code);
        Assert.Equal(1, faulting.Faults);
        Assert.Equal(oldHash, await Db.DocumentContentHashAsync(host.Database, "a.txt"));

        // The new chunk landed before the row was attempted (additions first), so the next run
        // finds both chunks by hash, embeds nothing, and finally writes the row.
        host.Generator.OnCall = null;
        var third = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, third.Outcome);
        Assert.Equal(1, third.DocumentsIndexed);
        Assert.Equal(0, third.EmbedCalls);
        Assert.Equal(2, third.ChunksUnchanged);
        Assert.NotEqual(oldHash, await Db.DocumentContentHashAsync(host.Database, "a.txt"));
        Assert.Equal(2, await Db.CountAsync(host.Database, Db.DataTable));
    }

    /// <summary>
    /// Spec 9.5's invariant at integration scope, over every SP3 write path there is: a first index
    /// of text, Markdown and the PDF satellite's output, an edit, a removal with its prune, a
    /// head-insert repair, then <c>PruneAsync</c>, <c>RemoveDocumentAsync</c> and
    /// <c>RemoveSourceAsync</c>. The spy fails the run if any open happens inside an SP3 transaction.
    /// </summary>
    [Fact]
    public async Task No_SP3_write_path_opens_a_connection_inside_an_SP3_transaction()
    {
        SpyEdgeDatabase? spy = null;
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            AfterIngestion = edge => edge.AddPdfExtractor(),
            WrapDatabase = inner => spy = new SpyEdgeDatabase(inner),
        });
        Assert.NotNull(spy);

        var items = CorpusRoundTripTests.TextCorpus
            .Concat(["pdf/minimal-text.pdf", "pdf/two-pages.pdf"])
            .Select(Extraction.FixtureCorpus.Item)
            .ToList();
        var prose = new RecordingSource("prose")
            .Add("long.txt", Prose.Document(20, WordsPerParagraph))
            .Add("gone.txt", "this one will be removed");

        var first = await host.Pipeline.RunAsync(IngestionSource.Items(items, "corpus"), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, first.Outcome);
        await host.Pipeline.RunAsync(prose, cancellationToken: Token);

        // An edit, a removal and a head insert in one run: additions, deletions, repairs, a prune.
        prose.Replace("long.txt", Prose.Paragraph(99, WordsPerParagraph) + "\n\n" + Prose.Document(20, WordsPerParagraph));
        prose.Remove("gone.txt");
        var second = await host.Pipeline.RunAsync(prose, cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
        Assert.Equal(20, second.ChunksRepaired);
        Assert.Equal(1, second.DocumentsRemoved);

        Assert.Equal(0, await host.Pipeline.PruneAsync(prose, cancellationToken: Token));
        Assert.Equal(1, await host.Pipeline.RemoveDocumentAsync("prose", "long.txt", ct: Token));
        Assert.True(await host.Pipeline.RemoveSourceAsync("corpus", ct: Token) > 0);

        Assert.True(spy.TransactionCount > 0, "the runs opened no transaction at all");
        Assert.False(spy.NestedOpenObserved, "an SP2 collection call happened inside an SP3 transaction");
        Assert.Equal(0, await Db.CountAsync(host.Database, Db.DataTable));
    }

    /// <summary>
    /// Cancel mid-run is <c>Cancelled</c>, and the resume produces the same final state as an
    /// uninterrupted run over the same corpus on a second host - every data-table row and every
    /// vector, compared as text.
    /// </summary>
    [Fact]
    public async Task Cancel_mid_run_resumes_to_the_same_final_state_as_an_uninterrupted_run()
    {
        using var interrupted = await IntegrationHost.StartAsync();
        using var uninterrupted = await IntegrationHost.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var source = new CancelAfterSource(cancellation, cancelAt: 3, id: "docs");
        for (var i = 0; i < 6; i++)
        {
            source.Add($"doc{i}.md", $"# Title {i}\n\n" + Prose.Document(3, 60, firstIndex: i * 10));
        }

        var cancelled = await interrupted.Pipeline.RunAsync(source, cancellationToken: cancellation.Token);

        Assert.Equal(IngestionRunOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal("caller:stop", cancelled.SuspendReason);
        Assert.InRange(cancelled.DocumentsIndexed, 1, 3);

        var resumed = await interrupted.Pipeline.RunAsync(source.AsRecordingSource(), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, resumed.Outcome);
        Assert.Equal(6, resumed.DocumentsSeen);
        Assert.Equal(6 - cancelled.DocumentsIndexed, resumed.DocumentsIndexed);

        var straight = await uninterrupted.Pipeline.RunAsync(source.AsRecordingSource(), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, straight.Outcome);

        var left = await Db.DumpAsync(interrupted.Database);
        var right = await Db.DumpAsync(uninterrupted.Database);
        Assert.NotEmpty(left);
        Assert.Equal(right, left);
        SqliteConnection.ClearAllPools();
    }
}
