using Microsoft.ML.OnnxRuntime;

namespace Qavren.Edge.Onnx;

/// <summary>
/// What the loaded graph actually declares, read from ORT after creation rather than assumed from
/// a preset. This is what turns "somebody pointed a preset at a different export" from silently
/// wrong vectors into a message naming the file.
/// </summary>
/// <param name="InputNames">Every input ORT demands in the feed, in the graph's declared order.</param>
/// <param name="OutputNames">Every output the graph produces.</param>
/// <param name="InputTypes">Input name to ORT's element type, as a string.</param>
/// <param name="OutputShapes">
/// Output name to its declared dimensions. A symbolic dimension is -1: only the static ones can be
/// compared with a preset's declared dimension count.
/// </param>
public sealed record OnnxSessionSignature(
    IReadOnlyList<string> InputNames,
    IReadOnlyList<string> OutputNames,
    IReadOnlyDictionary<string, string> InputTypes,
    IReadOnlyDictionary<string, IReadOnlyList<long>> OutputShapes);

/// <summary>One session, as diagnostics and a consumer see it.</summary>
/// <param name="ModelId">The registered model id.</param>
/// <param name="GraphPath">The absolute path ORT was handed.</param>
/// <param name="GraphSha256">The graph's lowercase-hex digest.</param>
/// <param name="Signature">What the graph declares.</param>
/// <param name="ExecutionProviders">Every provider attempt, in order, and the one that was accepted.</param>
/// <param name="LoadDuration">How long <c>new InferenceSession</c> plus validation took.</param>
/// <param name="LoadedAtUtc">When the current session was created.</param>
/// <param name="LoadCount">How many times this model has been loaded in this process.</param>
/// <param name="ActiveLeases">How many leases are outstanding right now.</param>
/// <param name="IsLoaded">False once the session has been dropped and not yet reloaded.</param>
public sealed record OnnxSessionInfo(
    string ModelId,
    string GraphPath,
    string GraphSha256,
    OnnxSessionSignature Signature,
    ExecutionProviderReport ExecutionProviders,
    TimeSpan LoadDuration,
    DateTimeOffset LoadedAtUtc,
    int LoadCount,
    int ActiveLeases,
    bool IsLoaded);

/// <summary>
/// A borrowed session. <see cref="Dispose"/> releases the lease; the underlying
/// <see cref="InferenceSession"/> is disposed only when the last lease returns after a drop.
/// </summary>
/// <remarks>
/// <see cref="InferenceSession"/> has a finalizer, so a dropped-but-undisposed session pins the
/// entire native graph until GC - jetsam bait on iOS - while disposing one under an in-flight
/// <c>Run</c> is a native access violation. The lease is what makes both impossible.
/// </remarks>
public sealed class OnnxSessionLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal OnnxSessionLease(InferenceSession session, OnnxSessionInfo info, Action release)
    {
        Session = session;
        Info = info;
        _release = release;
    }

    /// <summary>The borrowed session. Valid until this lease is disposed, and not one moment after.</summary>
    public InferenceSession Session { get; }

    /// <summary>The session's facts as of the moment the lease was taken.</summary>
    public OnnxSessionInfo Info { get; }

    /// <summary>Returns the lease. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Owns every <see cref="InferenceSession"/> in the process. There is deliberately NO byte-array
/// entry point: a <c>byte[]</c> overload holds the managed array and ORT's copy of the initializers
/// simultaneously during creation (about 180 MB transient for fp32 MiniLM) and degrades ORT's
/// CoreML cache key from the model-URL hash to a hash of graph inputs and node outputs, which
/// collides across quantization variants of one architecture. A consumer cannot take that path.
/// </summary>
public interface IOnnxSessionHost
{
    /// <summary>
    /// Awaits <c>IEdgeHost.EnsureStartedAsync</c>, provisions the model if needed, checks the memory
    /// budget, creates the session on first call, and returns a lease. Concurrent callers for one
    /// model share one session and one in-flight load.
    /// </summary>
    /// <param name="modelId">The registered model id.</param>
    /// <param name="cancellationToken">Cancellation. Also aborts a CoreML compile in flight.</param>
    /// <returns>A lease that must be disposed.</returns>
    ValueTask<OnnxSessionLease> AcquireAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>The session's facts, or null when this model has never been loaded.</summary>
    /// <param name="modelId">The registered model id.</param>
    /// <returns>The session info, or null.</returns>
    OnnxSessionInfo? Describe(string modelId);

    /// <summary>Every session this host knows about, loaded or dropped.</summary>
    IReadOnlyList<OnnxSessionInfo> Sessions { get; }

    /// <summary>
    /// Marks matching sessions for drop and disposes the idle ones. Returns the count actually
    /// released; leased sessions die as their leases return.
    /// </summary>
    /// <param name="includePinned">
    /// False - the memory-pressure path - skips sessions whose
    /// <c>OnnxSessionOptions.DropOnMemoryPressure</c> is false. True is shutdown.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many sessions were disposed synchronously.</returns>
    Task<int> DropAsync(bool includePinned = false, CancellationToken cancellationToken = default);
}
