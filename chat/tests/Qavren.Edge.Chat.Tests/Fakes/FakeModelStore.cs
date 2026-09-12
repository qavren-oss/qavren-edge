using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// Sub-project 2's model store, scripted. It exists to prove a negative more often than a positive:
/// every disk and consent refusal in spec section 15.3 has to happen <b>before the first byte</b>,
/// and <see cref="Ensures"/> is how a test says so.
/// </summary>
internal sealed class FakeModelStore : IOnnxModelStore
{
    private readonly string _directory;

    /// <summary>Creates the store.</summary>
    /// <param name="directory">The directory <see cref="EnsureAsync"/> claims to have filled.</param>
    public FakeModelStore(string directory) => _directory = directory;

    /// <summary>What <see cref="IsProvisioned"/> answers.</summary>
    public bool Provisioned { get; set; }

    /// <summary>How many times a transfer was actually started.</summary>
    public int Ensures { get; private set; }

    /// <summary>How many times a model was removed.</summary>
    public int Removals { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> ProvisionedModelIds => [];

    /// <inheritdoc />
    public ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Ensures++;

        return ValueTask.FromResult(new ProvisionedModel(
            manifest.ModelId,
            _directory,
            Path.Combine(_directory, manifest.GraphFile),
            manifest.Files[0].Sha256,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ModelProvisioningSource.AlreadyPresent,
            TimeSpan.Zero));
    }

    /// <inheritdoc />
    public bool IsProvisioned(OnnxModelManifest manifest) => Provisioned;

    /// <inheritdoc />
    public ProvisionedModel? TryGet(string modelId) => null;

    /// <inheritdoc />
    public ValueTask RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        Removals++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Sub-project 2's model paths, pointed at a temp directory.</summary>
/// <param name="root">The root both properties hang off.</param>
internal sealed class FakeModelPaths(string root) : IEdgeModelPaths
{
    /// <inheritdoc />
    public string Models => Ensure(Path.Combine(root, "models"));

    /// <inheritdoc />
    public string OrtCache => Ensure(Path.Combine(root, "ort-cache"));

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>
/// Sub-project 1's host, which the chat host awaits in <c>AcquireAsync</c> and never in
/// <c>PreloadAsync</c>.
/// </summary>
/// <remarks>
/// Every load-path assertion goes through <c>PreloadAsync</c> / <c>AcquireCoreAsync</c>, which is
/// the whole point: a test that went through <c>AcquireAsync</c> would be asserting sub-project 1's
/// startup as well, and the 7001 arm exists precisely to catch a host used <b>without</b> its
/// startup task.
/// </remarks>
internal sealed class NoOpEdgeHost : IEdgeHost
{
    /// <inheritdoc />
    public Task Started => Task.CompletedTask;

    /// <inheritdoc />
    public void Start()
    {
        // Nothing to start.
    }

    /// <inheritdoc />
    public ValueTask EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
