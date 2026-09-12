using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 10.2's three evaluations and its reason strings. A caller deciding whether to reschedule
/// never has to parse a log line, so every path here produces its documented string.
/// </summary>
public sealed class SuspendReasonTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_budget_suspends_with_budget_documents()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "a").Add("b.txt", "b").Add("c.txt", "c");

        var result = await host.Pipeline.RunAsync(
            source,
            options: new IngestionRunOptions { Budget = new IngestionBudget { MaxDocuments = 2 } },
            cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("budget:documents", result.SuspendReason);
        Assert.Equal(2, result.DocumentsIndexed);
    }

    [Fact]
    public async Task A_chunk_budget_suspends_with_budget_chunks()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "a").Add("b.txt", "b").Add("c.txt", "c");

        var result = await host.Pipeline.RunAsync(
            source,
            options: new IngestionRunOptions { Budget = new IngestionBudget { MaxChunks = 1 } },
            cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("budget:chunks", result.SuspendReason);
    }

    [Fact]
    public async Task A_token_budget_suspends_with_budget_tokens()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "one two three").Add("b.txt", "four five");

        var result = await host.Pipeline.RunAsync(
            source,
            options: new IngestionRunOptions { Budget = new IngestionBudget { MaxTokens = 1 } },
            cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("budget:tokens", result.SuspendReason);
    }

    /// <summary>
    /// The <c>Quick</c> preset, metered. A corpus whose processing exceeds 20 s of
    /// <see cref="ManualTimeProvider"/> time suspends with <c>budget:duration</c>.
    /// </summary>
    [Fact]
    public async Task A_Quick_run_over_twenty_seconds_suspends_with_budget_duration()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new SlowSource(host.Time, TimeSpan.FromSeconds(9), 5);

        var result = await host.Pipeline.RunAsync(
            source, options: new IngestionRunOptions { Budget = IngestionBudget.Quick }, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("budget:duration", result.SuspendReason);
    }

    /// <summary>
    /// The same preset over a corpus that finishes inside 20 s does NOT suspend — which is how
    /// <c>MaxDocuments = null</c> on the preset stops being an unwritten assumption.
    /// </summary>
    [Fact]
    public async Task A_Quick_run_over_five_hundred_documents_inside_the_window_does_not_suspend()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource();
        for (var i = 0; i < 500; i++)
        {
            source.Add($"doc{i}.txt", $"document number {i}");
        }

        var result = await host.Pipeline.RunAsync(
            source, options: new IngestionRunOptions { Budget = IngestionBudget.Quick }, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Null(result.SuspendReason);
        Assert.Equal(500, result.DocumentsIndexed);
    }

    [Fact]
    public async Task A_throttle_pause_suspends_by_default_and_carries_the_throttle_reason()
    {
        using var host = await IngestionTestHost.StartAsync(
            configure: edge => edge.UseIngestionThrottle(_ => new PausingThrottle("memory:floor")));
        var source = new RecordingSource().Add("a.txt", "alpha");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("memory:floor", result.SuspendReason);
        Assert.Contains(host.Logs.Lines, l => l.StartsWith("920|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throttle_batch_size_narrows_the_write_window()
    {
        using var host = await IngestionTestHost.StartAsync(
            o => o.WriteBatchSize = 8,
            configure: edge => edge.UseIngestionThrottle(_ => new FixedSizeThrottle(1)));

        var source = new RecordingSource().Add(
            "a.txt", string.Join(' ', Enumerable.Range(0, 900).Select(i => "word" + i)));

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // The throttle clamps the window to one chunk, so there is exactly one embed per chunk.
        Assert.True(result.ChunksAdded > 2, $"expected several chunks, got {result.ChunksAdded}");
        Assert.Equal(result.ChunksAdded, result.EmbedCalls);
    }

    /// <summary>
    /// Spec 10.2 step 1: a stop that came from LIFECYCLE is <c>Suspended</c>, and <c>Cancelled</c> is
    /// the caller-cancelled case. <c>memory:critical</c> is one of spec 10.2's own reason strings and
    /// wears no <c>"lifecycle:"</c> prefix, so inferring the outcome from the string reported a
    /// backgrounded, memory-pressured run as cancelled by its caller.
    /// </summary>
    [Fact]
    public async Task Critical_memory_pressure_suspends_with_memory_critical_not_cancels()
    {
        using var host = await IngestionTestHost.StartAsync();
        var observer = Observer(host);
        var source = new InterruptingSource(
            () => observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, CancellationToken.None), after: 1);
        source.Add("a.txt", "alpha").Add("b.txt", "beta").Add("c.txt", "gamma");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("memory:critical", result.SuspendReason);
    }

    [Fact]
    public async Task A_lifecycle_sleeping_stop_also_suspends()
    {
        using var host = await IngestionTestHost.StartAsync();
        var observer = Observer(host);
        var source = new InterruptingSource(
            () =>
            {
                // NOT awaited: OnSleepingAsync sets the stop flag synchronously and then waits for
                // the runner's next committed checkpoint, which cannot arrive while the runner is
                // blocked on this enumerator. The dangling wait is released by EndRun.
                _ = observer.OnSleepingAsync(CancellationToken.None);
                return Task.CompletedTask;
            },
            after: 1);
        source.Add("a.txt", "alpha").Add("b.txt", "beta").Add("c.txt", "gamma");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("lifecycle:sleeping", result.SuspendReason);
    }

    [Fact]
    public async Task RequestStop_by_the_CALLER_cancels_rather_than_suspends()
    {
        // The other half of the same gate: the caller's own stop is not a lifecycle stop.
        using var host = await IngestionTestHost.StartAsync();
        var source = new InterruptingSource(
            () =>
            {
                host.Pipeline.RequestStop("caller:stop");
                return Task.CompletedTask;
            },
            after: 1);
        source.Add("a.txt", "alpha").Add("b.txt", "beta").Add("c.txt", "gamma");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Cancelled, result.Outcome);
        Assert.Equal("caller:stop", result.SuspendReason);
    }

    private static IEdgeLifecycleObserver Observer(IngestionTestHost host) =>
        host.Services.GetServices<IEdgeLifecycleObserver>()
            .First(o => o.GetType().Name == "IngestionLifecycleObserver");

    [Fact]
    public async Task A_cancelled_run_reports_Cancelled_with_no_exception_by_default()
    {
        using var host = await IngestionTestHost.StartAsync();
        using var cts = new CancellationTokenSource();
        var source = new CancellingSource(cts, 1);
        source.Add("a.txt", "alpha").Add("b.txt", "beta");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: cts.Token);

        Assert.Equal(IngestionRunOutcome.Cancelled, result.Outcome);
        Assert.Equal("caller:stop", result.SuspendReason);
    }

    [Fact]
    public async Task ThrowOnCancellation_opts_into_the_exception()
    {
        using var host = await IngestionTestHost.StartAsync();
        using var cts = new CancellationTokenSource();
        var source = new CancellingSource(cts, 1);
        source.Add("a.txt", "alpha").Add("b.txt", "beta");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Pipeline.RunAsync(
                source,
                options: new IngestionRunOptions { ThrowOnCancellation = true },
                cancellationToken: cts.Token));
    }

    private sealed class PausingThrottle(string reason) : IIngestionThrottle
    {
        public string Name => "pausing";

        public IngestionThrottleDecision Evaluate(IngestionThrottleContext context) =>
            new(context.ConfiguredBatchSize, TimeSpan.Zero, Pause: true, reason);
    }

    private sealed class FixedSizeThrottle(int batchSize) : IIngestionThrottle
    {
        public string Name => "fixed-size";

        public IngestionThrottleDecision Evaluate(IngestionThrottleContext context) =>
            IngestionThrottleDecision.Proceed(batchSize);
    }

    private sealed class SlowSource(ManualTimeProvider time, TimeSpan perDocument, int count) : IngestionSource
    {
        public override string Id => "slow";

#pragma warning disable CS1998
        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                time.Advance(perDocument);
                var bytes = System.Text.Encoding.UTF8.GetBytes("document " + i);
                yield return new DocumentSourceItem(
                    $"slow{i}.txt",
                    IngestionMediaTypes.PlainText,
                    _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
                {
                    SizeBytes = bytes.Length,
                };
            }
        }
#pragma warning restore CS1998
    }

    /// <summary>Runs <paramref name="interrupt"/> just before yielding item <paramref name="after"/>.</summary>
    private sealed class InterruptingSource(Func<Task> interrupt, int after) : IngestionSource
    {
        private readonly List<(string Id, byte[] Bytes)> _items = [];

        public override string Id => "interrupting";

        public InterruptingSource Add(string id, string text)
        {
            _items.Add((id, System.Text.Encoding.UTF8.GetBytes(text)));
            return this;
        }

        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = 0;
            foreach (var (id, bytes) in _items)
            {
                if (index++ == after)
                {
                    await interrupt().ConfigureAwait(false);
                }

                yield return new DocumentSourceItem(
                    id,
                    IngestionMediaTypes.PlainText,
                    _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
                {
                    SizeBytes = bytes.Length,
                };
            }
        }
    }

    private sealed class CancellingSource(CancellationTokenSource source, int after) : IngestionSource
    {
        private readonly List<(string Id, byte[] Bytes)> _items = [];

        public override string Id => "cancelling";

        public CancellingSource Add(string id, string text)
        {
            _items.Add((id, System.Text.Encoding.UTF8.GetBytes(text)));
            return this;
        }

#pragma warning disable CS1998
        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = 0;
            foreach (var (id, bytes) in _items)
            {
                if (index++ == after)
                {
                    await source.CancelAsync().ConfigureAwait(false);
                }

                yield return new DocumentSourceItem(
                    id,
                    IngestionMediaTypes.PlainText,
                    _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
                {
                    SizeBytes = bytes.Length,
                };
            }
        }
#pragma warning restore CS1998
    }
}
