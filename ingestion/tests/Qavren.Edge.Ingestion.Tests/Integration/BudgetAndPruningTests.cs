using Qavren.Edge.Ingestion.Tests.Runtime;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 10.2 and 9.6 at integration scope: a document budget suspends on a committed boundary and
/// skips the sweep, the resume finishes the job at no extra embedding cost, a deleted source file
/// is swept only by a <c>Completed</c> run or by <c>PruneAsync</c>, and critical memory pressure
/// mid-run suspends into a resumable state. Task 7.1 Step 4.
/// </summary>
public sealed class BudgetAndPruningTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RecordingSource SixDocuments(string id = "memory")
    {
        var source = new RecordingSource(id);
        for (var i = 0; i < 6; i++)
        {
            source.Add($"doc{i}.txt", Prose.Paragraph(i, 40));
        }

        return source;
    }

    [Fact]
    public async Task MaxDocuments_suspends_skips_pruning_and_the_resume_costs_no_extra_embeds()
    {
        using var budgeted = await IntegrationHost.StartAsync();
        using var unbudgeted = await IntegrationHost.StartAsync();

        // A stale row to prune: "gone.txt" is indexed once and then disappears from the source.
        var stale = new RecordingSource().Add("gone.txt", "this document will vanish");
        var seed = await budgeted.Pipeline.RunAsync(stale, cancellationToken: Token);
        Assert.Equal(1, seed.EmbedCalls);
        Assert.Equal(1, await Db.CountAsync(budgeted.Database, Db.DocumentTable));

        var source = SixDocuments();
        var callsBefore = budgeted.Generator.CallCount;

        var suspended = await budgeted.Pipeline.RunAsync(
            source,
            options: new IngestionRunOptions { Budget = new IngestionBudget { MaxDocuments = 3 } },
            cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, suspended.Outcome);
        Assert.Equal("budget:documents", suspended.SuspendReason);
        Assert.Equal(3, suspended.DocumentsIndexed);
        Assert.Equal(3, suspended.EmbedCalls);

        // Pruning was skipped: the stale row and its chunk are still there.
        Assert.Equal(0, suspended.DocumentsRemoved);
        Assert.NotNull(await Db.DocumentContentHashAsync(budgeted.Database, "gone.txt"));
        Assert.Single(await Db.KeysAsync(budgeted.Database, "gone.txt"));
        Assert.DoesNotContain(budgeted.Logs.Lines, l => l.StartsWith("912|", StringComparison.Ordinal));

        var resumed = await budgeted.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, resumed.Outcome);
        Assert.Equal(3, resumed.DocumentsSkipped);
        Assert.Equal(3, resumed.DocumentsIndexed);
        Assert.Equal(1, resumed.DocumentsRemoved);
        Assert.Null(await Db.DocumentContentHashAsync(budgeted.Database, "gone.txt"));
        Assert.Empty(await Db.KeysAsync(budgeted.Database, "gone.txt"));

        // Total embeds across the two runs equal a single unbudgeted run over the same corpus.
        var straight = await unbudgeted.Pipeline.RunAsync(SixDocuments(), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, straight.Outcome);
        Assert.Equal(straight.EmbedCalls, suspended.EmbedCalls + resumed.EmbedCalls);
        Assert.Equal(straight.EmbedCalls, budgeted.Generator.CallCount - callsBefore);
        Assert.Equal(await Db.CountAsync(unbudgeted.Database, Db.DataTable), await Db.CountAsync(budgeted.Database, Db.DataTable));
    }

    [Fact]
    public async Task A_deleted_source_file_is_swept_by_a_Completed_run_not_a_Suspended_one_and_by_PruneAsync()
    {
        using var host = await IntegrationHost.StartAsync();
        var source = new RecordingSource()
            .Add("a.txt", "alpha document")
            .Add("b.txt", "beta document")
            .Add("c.txt", "gamma document");

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(3, first.DocumentsIndexed);

        // b.txt is gone. A SUSPENDED run over the remainder must not sweep it: a partial
        // enumeration is not evidence a document is gone.
        source.Remove("b.txt");
        var suspended = await host.Pipeline.RunAsync(
            source,
            options: new IngestionRunOptions { Budget = new IngestionBudget { MaxDocuments = 1 } },
            cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, suspended.Outcome);
        Assert.Equal(0, suspended.DocumentsRemoved);
        Assert.Single(await Db.KeysAsync(host.Database, "b.txt"));
        Assert.NotNull(await Db.DocumentContentHashAsync(host.Database, "b.txt"));

        // PruneAsync sweeps it with no write phase.
        var callsBefore = host.Generator.CallCount;
        Assert.Equal(1, await host.Pipeline.PruneAsync(source, cancellationToken: Token));
        Assert.Equal(callsBefore, host.Generator.CallCount);
        Assert.Empty(await Db.KeysAsync(host.Database, "b.txt"));
        Assert.Null(await Db.DocumentContentHashAsync(host.Database, "b.txt"));
        Assert.Contains(host.Logs.Lines, l => l.StartsWith("912|", StringComparison.Ordinal));

        // And a COMPLETED run sweeps the next casualty on its own.
        source.Remove("c.txt");
        var completed = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, completed.Outcome);
        Assert.Equal(1, completed.DocumentsRemoved);
        Assert.Empty(await Db.KeysAsync(host.Database, "c.txt"));
        Assert.Equal(1, await Db.CountAsync(host.Database, Db.DocumentTable));
        Assert.Equal(1, await Db.CountAsync(host.Database, Db.DataTable));
    }

    /// <summary>
    /// Spec 12's <c>MemoryPressure(Critical)</c> row, raised through SP1's hub - the real chain,
    /// not SP3's observer in isolation - while a run is parked between two documents.
    /// </summary>
    [Fact]
    public async Task Critical_memory_pressure_mid_run_suspends_with_memory_critical_into_a_resumable_state()
    {
        using var host = await IntegrationHost.StartAsync();
        var gated = new GatedSource(gateAt: 2);
        for (var i = 0; i < 5; i++)
        {
            gated.Add($"doc{i}.txt", Prose.Paragraph(i, 30));
        }

        var run = host.Pipeline.RunAsync(gated, cancellationToken: Token);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30), Token).ConfigureAwait(true);

        await host.Lifecycle.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, Token);
        gated.Release();

        var suspended = await run.ConfigureAwait(true);

        Assert.Equal(IngestionRunOutcome.Suspended, suspended.Outcome);
        Assert.Equal("memory:critical", suspended.SuspendReason);
        Assert.Equal(2, suspended.DocumentsIndexed);
        Assert.Equal(2, await Db.CountAsync(host.Database, Db.DocumentTable));

        // Resumable: Resumed clears the shrink, and the next run finishes the other three.
        await host.Lifecycle.RaiseResumedAsync(Token);
        var plain = new RecordingSource("gated");
        for (var i = 0; i < 5; i++)
        {
            plain.Add($"doc{i}.txt", Prose.Paragraph(i, 30));
        }

        var resumed = await host.Pipeline.RunAsync(plain, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, resumed.Outcome);
        Assert.Equal(2, resumed.DocumentsSkipped);
        Assert.Equal(3, resumed.DocumentsIndexed);
        Assert.Equal(5, await Db.CountAsync(host.Database, Db.DocumentTable));
        Assert.Equal(5, await Db.CountAsync(host.Database, Db.DataTable));
    }
}
