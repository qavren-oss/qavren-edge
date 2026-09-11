using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Embeddings.Tests.Fakes;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Tests.Tier3;

/// <summary>
/// A container over the real int8 MiniLM staged in <c>QAVREN_EDGE_MODEL_DIR</c>. Built inside a
/// test body, never in a constructor - see <see cref="ModelAvailable"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here can download.</b> <c>AddOnnxEmbeddings</c> registers the Hugging Face source
/// only when <c>OnnxEmbeddingOptions.ModelSource</c> is null; setting it to a
/// <see cref="FileOnnxModelSource"/> over the staged directory means the HTTP source is never in
/// the container at all. That is stronger than the plan's <c>AllowDownload = false</c>, which only
/// disarms a source that is registered: the workflow has already fetched and hash-verified these
/// files, and a test that could silently re-download would defeat the cache the lane is built on.
/// </para>
/// <para>
/// The model root is a fresh scratch directory, deleted on dispose. The staged directory itself is
/// never written to - <c>FileOnnxModelSource</c> copies out of it into
/// <c>&lt;Models&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c> - so a cached CI directory survives the run
/// unchanged.
/// </para>
/// </remarks>
internal sealed class Tier3Host : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _root;

    private Tier3Host(ServiceProvider services, string root)
    {
        _services = services;
        _root = root;
    }

    /// <summary>The container.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The staged model directory this process was pointed at.</summary>
    public static string StagedDirectory =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_MODEL_DIR") is { Length: > 0 } dir
            ? dir
            : throw new InvalidOperationException(
                "QAVREN_EDGE_MODEL_DIR is not set. Tier 3 runs only with a staged model; every " +
                "other lane skips it.");

    /// <summary>Builds the container. Lazy: no model is provisioned until the first embed.</summary>
    /// <returns>The host.</returns>
    public static Tier3Host Build()
    {
        var staged = StagedDirectory;
        var root = Path.Combine(Path.GetTempPath(), "qedge-tier3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var modelPaths = new TempModelPaths(root);

        var services = new ServiceCollection();
        services.AddLogging();

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first.
        services.AddSingleton<IEdgePaths>(new TempPaths(root));

        services.AddQavrenEdge(edge =>
        {
            edge.UseModelPaths(modelPaths);
            edge.AddOnnxEmbeddings(o =>
            {
                o.Preset = EmbeddingPresets.MiniLmL6V2Int8;
                o.ModelSource = new FileOnnxModelSource(modelPaths, staged);
            });
        });

        return new Tier3Host(services.BuildServiceProvider(), root);
    }

    /// <summary>The document-side generator.</summary>
    /// <returns>The generator the four-call shape would resolve.</returns>
    public IEmbeddingGenerator<string, Embedding<float>> Generator()
        => _services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked ORT cache file must not fail a green test run.
        }
    }
}
