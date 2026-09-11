namespace Qavren.Edge.Ingestion;

/// <summary>
/// What a pause means. <see cref="Suspend"/> ends the run on a committed boundary;
/// <see cref="Wait"/> delays and re-evaluates (spec 10.2).
/// </summary>
public enum ThrottlePauseBehavior
{
    /// <summary>End the run with <see cref="IngestionRunOutcome.Suspended"/>. The default.</summary>
    Suspend,

    /// <summary>Delay, then evaluate again.</summary>
    Wait,
}

/// <summary>What the runner tells a throttle about the run so far.</summary>
/// <param name="ConfiguredBatchSize">The registered <c>WriteBatchSize</c>.</param>
/// <param name="CurrentBatchSize">The batch in force, after memory pressure has shrunk it.</param>
/// <param name="DocumentsProcessed">Documents finished so far.</param>
/// <param name="ChunksWritten">Chunks upserted so far.</param>
/// <param name="Elapsed">Wall time since the run started.</param>
public readonly record struct IngestionThrottleContext(
    int ConfiguredBatchSize,
    int CurrentBatchSize,
    int DocumentsProcessed,
    long ChunksWritten,
    TimeSpan Elapsed);

/// <summary>
/// The tri-state spec 10.2's table produces. <see cref="Pause"/> wins over <see cref="Delay"/>
/// wins over <see cref="BatchSize"/>; <see cref="Reason"/> is one of that table's strings and
/// becomes <c>IngestionRunResult.SuspendReason</c> on a suspend.
/// </summary>
/// <param name="BatchSize">The batch to use for the next write window. Positive.</param>
/// <param name="Delay">How long to wait first. <see cref="TimeSpan.Zero"/> for none.</param>
/// <param name="Pause">Stop ingesting for now.</param>
/// <param name="Reason">The table's reason string, or null when proceeding.</param>
public readonly record struct IngestionThrottleDecision(
    int BatchSize, TimeSpan Delay, bool Pause, string? Reason)
{
    /// <summary>No signal: the configured batch, no delay, no pause.</summary>
    public static IngestionThrottleDecision Proceed(int batchSize) =>
        new(batchSize, TimeSpan.Zero, Pause: false, Reason: null);
}

/// <summary>A policy consulted before each document and before each write window.</summary>
public interface IIngestionThrottle
{
    /// <summary>Appears in diagnostics as <c>throttle</c>.</summary>
    string Name { get; }

    /// <summary>Called before each document and before each write window. Must not block.</summary>
    IngestionThrottleDecision Evaluate(IngestionThrottleContext context);
}

/// <summary>The core's default: always the configured batch, no delay, never pauses.</summary>
public sealed class FixedIngestionThrottle : IIngestionThrottle
{
    /// <inheritdoc />
    public string Name => "fixed";

    /// <inheritdoc />
    public IngestionThrottleDecision Evaluate(IngestionThrottleContext context) =>
        IngestionThrottleDecision.Proceed(context.ConfiguredBatchSize);
}
