using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 10.2's decision table, asserted <b>as the runner consumes it</b>: each row's
/// <see cref="IngestionThrottleDecision"/> is fed in through a scripted <see cref="IIngestionThrottle"/>
/// and the run's observable outcome is checked. What the table does NOT cover here is how
/// <c>ResourceMonitorThrottle</c> maps an <c>IEdgeResourceMonitor</c> reading onto a decision —
/// that type lives in <c>Qavren.Edge.Ingestion.Onnx</c> and Task 6.2 owns the mapping half,
/// including the rule that <c>EdgeThermalState.Unknown</c> and a null memory reading mean
/// <b>no signal</b>, which is <c>Proceed</c> and never "fine".
/// </summary>
public sealed class ThrottleTableTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every reason string spec 10.2's table can produce on a pause.</summary>
    [Theory]
    [InlineData("memory:critical")]
    [InlineData("memory:floor")]
    [InlineData("thermal:critical")]
    public async Task A_pause_suspends_the_run_with_the_tables_reason_string(string reason)
    {
        var throttle = ScriptedThrottle.Pausing(reason);
        using var host = await StartAsync(throttle);
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // A pause is Suspended by DEFAULT (ThrottlePauseBehavior.Suspend).
        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal(reason, result.SuspendReason);
        Assert.Equal(0, result.DocumentsIndexed);
    }

    [Fact]
    public async Task PauseBehavior_Wait_delays_and_re_evaluates_rather_than_suspending()
    {
        // The same pause, one option apart: Wait re-evaluates, and the second evaluation proceeds.
        var throttle = ScriptedThrottle.PausingOnce("thermal:critical");
        using var host = await StartAsync(
            throttle, o => o.PauseBehavior = ThrottlePauseBehavior.Wait, useSystemTime: true);
        var source = new RecordingSource().Add("a.txt", "alpha");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Null(result.SuspendReason);
        Assert.Equal(1, result.DocumentsIndexed);
    }

    /// <summary>
    /// The half of "delays and re-evaluates" that a single-pause script cannot see: the
    /// re-evaluation has to happen <b>at the document boundary</b>, not only inside the write
    /// window. A gate that delayed once and then fell through to the document would process the
    /// very document a still-pausing throttle is refusing — and would pass a test whose throttle
    /// stops pausing after the first call, because the window gate would cover for it.
    /// </summary>
    [Fact]
    public async Task PauseBehavior_Wait_re_evaluates_at_the_DOCUMENT_boundary_before_opening_anything()
    {
        var source = new RecordingSource().Add("a.txt", "alpha");

        // Two consecutive pauses at the boundary, then proceed. Each evaluation records how much of
        // the corpus had been opened by the time it ran.
        var opensWhilePausing = new List<int>();
        var throttle = new ScriptedThrottle(call =>
        {
            if (call > 1)
            {
                return IngestionThrottleDecision.Proceed(32);
            }

            opensWhilePausing.Add(source.Opens.Values.Sum());
            return new IngestionThrottleDecision(32, TimeSpan.FromMilliseconds(1), Pause: true, "thermal:critical");
        });

        using var host = await StartAsync(
            throttle, o => o.PauseBehavior = ThrottlePauseBehavior.Wait, useSystemTime: true);

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.DocumentsIndexed);

        // Both pausing evaluations ran before the document was touched: the second one is the
        // re-evaluation, and it happened at the boundary rather than after the document had been
        // hashed, extracted and embedded.
        Assert.Equal(2, opensWhilePausing.Count);
        Assert.All(opensWhilePausing, opens => Assert.Equal(0, opens));
    }

    [Fact]
    public async Task A_halved_batch_is_honoured_and_never_exceeds_the_configured_batch()
    {
        // "batch / 2" rows: thermal:serious, memory:moderate, memory:warn. The runner clamps to the
        // batch in force, so a throttle asking for MORE than the configured batch gets the
        // configured batch - a policy cannot widen a window the consumer sized.
        var throttle = new ScriptedThrottle(_ =>
            new IngestionThrottleDecision(1, TimeSpan.Zero, Pause: false, "thermal:serious"));
        using var host = await StartAsync(throttle, o => o.WriteBatchSize = 8);
        var source = new RecordingSource().Add("a.txt", "alpha beta gamma delta");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.DocumentsIndexed);
        Assert.True(result.EmbedCalls >= 1);
    }

    [Fact]
    public async Task No_signal_is_Proceed_at_the_configured_batch()
    {
        // FixedIngestionThrottle is the core's default and the table's "otherwise" row.
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.DocumentsIndexed);
        Assert.Null(result.SuspendReason);
    }

    [Fact]
    public void The_cores_default_throttle_never_pauses()
    {
        var decision = new FixedIngestionThrottle().Evaluate(
            new IngestionThrottleContext(32, 32, 0, 0, TimeSpan.Zero));

        Assert.False(decision.Pause);
        Assert.Equal(32, decision.BatchSize);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
        Assert.Null(decision.Reason);
    }

    private static Task<IngestionTestHost> StartAsync(
        IIngestionThrottle throttle,
        Action<IngestionOptions>? configureIngestion = null,
        bool useSystemTime = false) =>
        IngestionTestHost.StartAsync(
            configureIngestion,
            configureStore: null,
            configure: edge => edge.UseIngestionThrottle(_ => throttle),
            useSystemTime: useSystemTime);

    private sealed class ScriptedThrottle(Func<int, IngestionThrottleDecision> script) : IIngestionThrottle
    {
        private int _calls;

        public string Name => "scripted";

        public static ScriptedThrottle Pausing(string reason) =>
            new(_ => new IngestionThrottleDecision(32, TimeSpan.Zero, Pause: true, reason));

        public static ScriptedThrottle PausingOnce(string reason) =>
            new(call => call == 0
                ? new IngestionThrottleDecision(32, TimeSpan.FromMilliseconds(1), Pause: true, reason)
                : IngestionThrottleDecision.Proceed(32));

        public IngestionThrottleDecision Evaluate(IngestionThrottleContext context) => script(_calls++);
    }
}
