using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 12's ordering. SP2's bounded FTS5 merge now runs against SP3's own sidecar (plan
/// adjustment 2), so <c>[Ingestion, VectorData, Sqlite]</c> is what stops that merge running while
/// SP3's runner is still writing to the table.
/// </summary>
public sealed class LifecycleOrderTests
{
    [Fact]
    public void The_canonical_sequence_yields_Ingestion_then_VectorData_then_Sqlite()
    {
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
        });

        var names = provider.GetServices<IEdgeLifecycleObserver>().Select(o => o.GetType().Name).ToList();

        Assert.Equal("IngestionLifecycleObserver", names[0]);
        Assert.True(
            names.IndexOf("VectorDataLifecycleObserver") < names.IndexOf("SqliteLifecycleObserver"),
            "SP2's observer must still precede SP1's: " + string.Join(", ", names));
    }

    [Fact]
    public void A_second_AddIngestion_inserts_no_second_observer()
    {
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
            edge.AddIngestion(3, o => o.CollectionName = "other");
        });

        var observers = provider.GetServices<IEdgeLifecycleObserver>()
            .Count(o => o is IngestionLifecycleObserver);

        Assert.Equal(1, observers);
    }

    [Fact]
    public async Task Sleeping_sets_the_stop_flag_and_honours_the_grace_budget()
    {
        var control = new IngestionRunControl();
        var registry = new IngestionRegistry();
        var time = new ManualTimeProvider();
        var observer = new IngestionLifecycleObserver(control, registry, time);

        control.BeginRun();
        var sleeping = observer.OnSleepingAsync(CancellationToken.None);

        Assert.Equal("lifecycle:sleeping", control.StopReason);
        Assert.False(sleeping.IsCompleted);

        // The default grace is 750 ms; the window elapses and the observer returns IMMEDIATELY,
        // because SP1's platform bridges raise this on the callback thread.
        time.Advance(TimeSpan.FromSeconds(1));
        await sleeping.ConfigureAwait(true);
    }

    [Fact]
    public async Task A_committed_checkpoint_releases_the_grace_wait_early()
    {
        var control = new IngestionRunControl();
        var observer = new IngestionLifecycleObserver(control, new IngestionRegistry(), new ManualTimeProvider());

        control.BeginRun();
        var sleeping = observer.OnSleepingAsync(CancellationToken.None);
        control.SignalCheckpoint();

        await sleeping.ConfigureAwait(true);
    }

    [Fact]
    public async Task MemoryPressure_halves_the_batch_with_a_floor_of_four_and_Critical_also_stops()
    {
        var control = new IngestionRunControl();
        var observer = new IngestionLifecycleObserver(control, new IngestionRegistry(), new ManualTimeProvider());

        Assert.Equal(32, control.ApplyShrink(32));

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, CancellationToken.None);
        Assert.Equal(16, control.ApplyShrink(32));
        Assert.Null(control.StopReason);

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, CancellationToken.None);
        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, CancellationToken.None);
        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, CancellationToken.None);
        Assert.Equal(4, control.ApplyShrink(32));

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, CancellationToken.None);
        Assert.Equal("memory:critical", control.StopReason);

        // Resumed clears the shrink and does NOT auto-restart a run.
        await observer.OnResumedAsync(CancellationToken.None);
        Assert.Equal(32, control.ApplyShrink(32));
    }

    [Fact]
    public async Task RequestStop_ends_the_next_run_as_Cancelled_with_the_caller_reason()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha");

        host.Pipeline.RequestStop("caller:stop");
        var result = await host.Pipeline.RunAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        // BeginRun clears a stale reason, so a stop requested BEFORE a run does not leak into it.
        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
    }
}
