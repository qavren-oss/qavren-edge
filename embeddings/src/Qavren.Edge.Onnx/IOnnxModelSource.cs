namespace Qavren.Edge.Onnx;

/// <summary>
/// One way of getting a model's files onto the device. Sources are probed in registration order
/// and every one of them is contractually required to produce a real filesystem path, even on
/// Android where an app-package asset is a pathless <c>AssetManager</c> stream: spec 9.1 creates
/// sessions from paths, never from byte arrays.
/// </summary>
public interface IOnnxModelSource
{
    /// <summary>A short stable name, used in logs and in provisioning failures.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this source could supply the manifest at all. Cheap and synchronous: it inspects
    /// the manifest, never the network.
    /// </summary>
    /// <param name="manifest">The manifest to consider.</param>
    /// <returns>True when this source is worth asking.</returns>
    bool CanProvide(OnnxModelManifest manifest);

    /// <summary>
    /// Materialises every file into <c>&lt;Models&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c>, verifying
    /// each digest, and returns the resolved paths.
    /// </summary>
    /// <param name="manifest">The manifest to satisfy.</param>
    /// <param name="progress">Optional progress, for a determinate bar.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The provisioned model.</returns>
    ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The one thing that decides whether a model is on disk. Idempotent, concurrency-safe, and the
/// only writer of the <c>.qavren-model.json</c> marker.
/// </summary>
public interface IOnnxModelStore
{
    /// <summary>
    /// Idempotent and concurrency-safe: one <c>SemaphoreSlim</c> per model id, so eight concurrent
    /// callers produce one provisioning. Sources are probed in registration order.
    /// </summary>
    /// <param name="manifest">The manifest to satisfy.</param>
    /// <param name="progress">Optional progress.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The provisioned model.</returns>
    ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the marker exists and every file's length matches. Deliberately NOT a re-hash: full
    /// re-hashing on every launch is a startup tax paid for a case the provisioning-time verify
    /// already covers.
    /// </summary>
    /// <param name="manifest">The manifest to check.</param>
    /// <returns>True when the model is ready.</returns>
    bool IsProvisioned(OnnxModelManifest manifest);

    /// <summary>The result of the last successful <see cref="EnsureAsync"/> for this id, if any.</summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The provisioned model, or null.</returns>
    ProvisionedModel? TryGet(string modelId);

    /// <summary>Deletes the model directory and its CoreML cache subtree.</summary>
    /// <param name="modelId">The model id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes once both trees are gone.</returns>
    ValueTask RemoveAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>Every model id this store has provisioned in this process.</summary>
    IReadOnlyList<string> ProvisionedModelIds { get; }
}
