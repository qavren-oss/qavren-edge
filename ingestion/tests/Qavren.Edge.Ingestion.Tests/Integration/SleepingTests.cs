using System.Diagnostics;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 12 under plan adjustment 2. The observer chain is <c>[Ingestion, VectorData, Sqlite]</c>
/// and SP3's collection IS in SP2's registry, so the merge spec 12 assigned to SP3 belongs to
/// SP2's observer: its event 803 fires naming SP3's FTS table. That is the test that fails if a
/// later change moves SP3 back off <c>AddVectorCollectionMigration</c>. Task 7.1 Step 7.
/// </summary>
public sealed class SleepingTests
{
    private static readonly TimeSpan SleepGrace = TimeSpan.FromMilliseconds(300);

    /// <summary>SP2's observer budgets its merge at two seconds per registered collection.</summary>
    private static readonly TimeSpan Sp2MergeBudget = TimeSpan.FromSeconds(2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GatedSource Corpus(int gateAt)
    {
        var gated = new GatedSource(gateAt);
        for (var i = 0; i < 6; i++)
        {
            gated.Add($"doc{i}.txt", Prose.Document(3, 60, firstIndex: i * 10));
        }

        return gated;
    }

    [Fact]
    public async Task Sleeping_with_a_run_active_returns_inside_the_budget_and_SP2_merges_SP3s_sidecar()
    {
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            Ingestion = o => o.SleepGraceBudget = SleepGrace,
        });

        var gated = Corpus(gateAt: 2);
        var run = host.Pipeline.RunAsync(gated, cancellationToken: Token);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30), Token).ConfigureAwait(true);

        // The runner is parked on the source, so no checkpoint can arrive: SP3's link spends its
        // whole grace budget, then SP2 merges, then SP1 checkpoints WAL - and the sum has to fit.
        var clock = Stopwatch.StartNew();
        await host.Lifecycle.RaiseSleepingAsync(Token);
        clock.Stop();

        Assert.True(
            clock.Elapsed < SleepGrace + Sp2MergeBudget,
            $"RaiseSleepingAsync took {clock.Elapsed.TotalMilliseconds:F0} ms against a {SleepGrace.TotalMilliseconds:F0} ms " +
            $"grace budget plus SP2's {Sp2MergeBudget.TotalSeconds:F0} s merge. A failure here is a signal to lower " +
            "SleepGraceBudget, not to raise the ceiling.");

        // SP2's observer merged SP3's sidecar: 803 names SP3's FTS table.
        var merged = Assert.Single(host.Logs.Lines, l => l.StartsWith("803|", StringComparison.Ordinal));
        Assert.Contains("'" + Db.FullTextTable + "'", merged, StringComparison.Ordinal);

        // The stop flag reached the runner at a committed boundary: the two documents before the
        // gate are committed, the one at the gate never started, and the run is Suspended.
        gated.Release();
        var result = await run.ConfigureAwait(true);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("lifecycle:sleeping", result.SuspendReason);
        Assert.Equal(2, result.DocumentsIndexed);
        Assert.Equal(2, await Db.CountAsync(host.Database, Db.DocumentTable));
        Assert.Equal(result.ChunksAdded, await Db.CountAsync(host.Database, Db.DataTable));
    }

    [Fact]
    public async Task With_FullTextIndexed_off_there_is_no_FTS_table_and_no_merge()
    {
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            Ingestion = o =>
            {
                o.FullTextIndexed = false;
                o.SleepGraceBudget = SleepGrace;
            },
        });

        Assert.False(await Db.TableExistsAsync(host.Database, Db.FullTextTable));

        var gated = Corpus(gateAt: 2);
        var run = host.Pipeline.RunAsync(gated, cancellationToken: Token);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30), Token).ConfigureAwait(true);

        var clock = Stopwatch.StartNew();
        await host.Lifecycle.RaiseSleepingAsync(Token);
        clock.Stop();

        Assert.True(clock.Elapsed < SleepGrace + Sp2MergeBudget, $"RaiseSleepingAsync took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.DoesNotContain(host.Logs.Lines, l => l.StartsWith("803|", StringComparison.Ordinal));

        gated.Release();
        var result = await run.ConfigureAwait(true);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("lifecycle:sleeping", result.SuspendReason);
        Assert.False(await Db.TableExistsAsync(host.Database, Db.FullTextTable));
    }

    /// <summary>
    /// The half of the same guard with no run active: <c>Sleeping</c> returns at once, and SP2
    /// still merges the sidecar a completed run left behind.
    /// </summary>
    [Fact]
    public async Task Sleeping_after_a_completed_run_merges_and_returns_immediately()
    {
        using var host = await IntegrationHost.StartAsync();
        var result = await host.Pipeline.RunAsync(Corpus(gateAt: int.MaxValue), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);

        var clock = Stopwatch.StartNew();
        await host.Lifecycle.RaiseSleepingAsync(Token);
        clock.Stop();

        Assert.True(clock.Elapsed < Sp2MergeBudget, $"RaiseSleepingAsync took {clock.Elapsed.TotalMilliseconds:F0} ms with no run active");
        Assert.Contains(host.Logs.Lines, l => l.StartsWith("803|", StringComparison.Ordinal) && l.Contains(Db.FullTextTable, StringComparison.Ordinal));
        await Db.FtsIntegrityCheckAsync(host.Database);
    }
}
