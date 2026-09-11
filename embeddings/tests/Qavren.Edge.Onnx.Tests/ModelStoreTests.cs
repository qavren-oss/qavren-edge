using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Onnx.Internal;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// A model root under a temp directory. Shared by every store, source and session-host test in this
/// project: nothing here may touch the real LocalApplicationData.
/// </summary>
internal sealed class TempModelPaths : IEdgeModelPaths, IDisposable
{
    private readonly string _root;

    public TempModelPaths()
    {
        _root = Path.Combine(Path.GetTempPath(), "qedge-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Models => Ensure("models");

    public string OrtCache => Ensure("ort-cache");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Ensure(string leaf)
    {
        var directory = Path.Combine(_root, leaf);
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>An <see cref="ILogger{T}"/> that remembers every event id it was handed.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<EventId> _events = new();

    public IReadOnlyCollection<EventId> Events => _events;

    public bool Saw(int id) => _events.Any(e => e.Id == id);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => _events.Enqueue(eventId);
}

/// <summary>Spec 6.3 and 10.2's layout, marker, fast path, concurrency and purge.</summary>
public class ModelStoreTests : IDisposable
{
    private static readonly byte[] GraphBytes = Encoding.UTF8.GetBytes("pretend this is an onnx graph");
    private static readonly byte[] VocabBytes = Encoding.UTF8.GetBytes("hello\nworld\n");

    private readonly TempModelPaths _paths = new();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static OnnxModelManifest Manifest(string modelId = "tiny") => new()
    {
        ModelId = modelId,
        GraphFile = "onnx/model.onnx",
        SpdxLicense = "Apache-2.0",
        Files =
        [
            new OnnxModelFile("onnx/model.onnx", OnnxModelFileRole.Graph, GraphBytes.Length, Sha(GraphBytes)),
            new OnnxModelFile("vocab.txt", OnnxModelFileRole.Vocabulary, VocabBytes.Length, Sha(VocabBytes)),
        ],
    };

    /// <summary>Writes the manifest's files straight into the layout, counting how often it was asked.</summary>
    private sealed class CountingSource(IEdgeModelPaths paths) : IOnnxModelSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string Name => "counting";

        public bool CanProvide(OnnxModelManifest manifest) => true;

        public async ValueTask<ProvisionedModel> EnsureAsync(
            OnnxModelManifest manifest,
            IProgress<ModelProvisioningProgress>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            // Wide enough that eight callers genuinely overlap rather than serialise by luck.
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

            var directory = OnnxModelLayout.DirectoryFor(paths.Models, manifest);
            Directory.CreateDirectory(Path.Combine(directory, "onnx"));
            await File.WriteAllBytesAsync(
                OnnxModelLayout.FilePath(directory, "onnx/model.onnx"), GraphBytes, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                OnnxModelLayout.FilePath(directory, "vocab.txt"), VocabBytes, cancellationToken)
                .ConfigureAwait(false);

            return new ProvisionedModel(
                manifest.ModelId,
                directory,
                OnnxModelLayout.FilePath(directory, manifest.GraphFile),
                Sha(GraphBytes),
                OnnxModelLayout.Resolve(directory, manifest),
                // Bundled, not File, so a test can tell which source the store actually picked.
                ModelProvisioningSource.Bundled,
                TimeSpan.Zero);
        }
    }

    private OnnxModelStore Store(IOnnxModelSource source, ILogger<OnnxModelStore>? logger = null)
        => new(_paths, [source], logger ?? NullLogger<OnnxModelStore>.Instance);

    [Fact]
    public async Task TheLayoutIsModelIdThenSha16AndTheMarkerCarriesEveryFile()
    {
        var manifest = Manifest();
        using var store = Store(new CountingSource(_paths));

        var provisioned = await store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);

        var expected = Path.Combine(_paths.Models, "tiny", Sha(GraphBytes)[..16]);
        Assert.Equal(expected, provisioned.Directory);
        Assert.Equal(Path.Combine(expected, "onnx", "model.onnx"), provisioned.GraphPath);
        Assert.Equal(Sha(GraphBytes), provisioned.GraphSha256);
        Assert.Equal(Path.Combine(expected, "vocab.txt"), provisioned.Files["vocab.txt"]);

        var markerPath = Path.Combine(expected, ".qavren-model.json");
        Assert.True(File.Exists(markerPath));

        var marker = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken),
            ModelMarkerJsonContext.Default.ModelMarker);

        Assert.NotNull(marker);
        Assert.Equal("tiny", marker.ModelId);
        Assert.Equal(GraphBytes.Length + VocabBytes.Length, marker.TotalSizeBytes);
        Assert.Equal(2, marker.Files.Count);

        var graph = marker.Files.Single(f => f.RelativePath == "onnx/model.onnx");
        Assert.Equal(Sha(GraphBytes), graph.Sha256);
        Assert.Equal(GraphBytes.Length, graph.SizeBytes);
        Assert.Equal(provisioned.GraphPath, graph.Path);
        Assert.Equal(OnnxModelFileRole.Graph, graph.Role);
    }

    [Fact]
    public async Task EightConcurrentCallersProduceOneProvisioningAndOneProvisionedModel()
    {
        var manifest = Manifest();
        var source = new CountingSource(_paths);
        using var store = Store(source);

        var callers = new Task<ProvisionedModel>[8];
        for (var i = 0; i < callers.Length; i++)
        {
            callers[i] = Task.Run(
                () => store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken).AsTask(),
                TestContext.Current.CancellationToken);
        }

        var results = await Task.WhenAll(callers);

        Assert.Equal(1, source.Calls);
        foreach (var result in results)
        {
            Assert.Same(results[0], result);
        }
    }

    [Fact]
    public async Task TheFastPathReturnsAlreadyPresentWhenTheMarkerExistsAndEveryLengthMatches()
    {
        var manifest = Manifest();
        var source = new CountingSource(_paths);

        using (var first = Store(source))
        {
            await first.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);
        }

        // A second process's store over the same root. Full re-hashing on every launch is a startup
        // tax paid for a case the provisioning-time verify already covers.
        using var second = Store(source);
        Assert.True(second.IsProvisioned(manifest));

        var provisioned = await second.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ModelProvisioningSource.AlreadyPresent, provisioned.Source);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ATruncatedFileDefeatsTheFastPathAndReprovisions()
    {
        var manifest = Manifest();
        var source = new CountingSource(_paths);
        using var store = Store(source);

        var provisioned = await store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(provisioned.Files["vocab.txt"], [1, 2, 3], TestContext.Current.CancellationToken);

        Assert.False(store.IsProvisioned(manifest));

        var again = await store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, source.Calls);
        Assert.Equal(ModelProvisioningSource.Bundled, again.Source);
    }

    [Fact]
    public async Task RemoveDeletesTheModelDirectoryAndTheOrtCacheSubtreeAndLogs646()
    {
        var manifest = Manifest();
        var logger = new RecordingLogger<OnnxModelStore>();
        using var store = Store(new CountingSource(_paths), logger);

        var provisioned = await store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);

        var cache = Path.Combine(_paths.OrtCache, "tiny", Sha(GraphBytes)[..16]);
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(Path.Combine(cache, "compiled.mlmodelc"), "x", TestContext.Current.CancellationToken);

        await store.RemoveAsync("tiny", TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(provisioned.Directory));
        Assert.False(Directory.Exists(Path.Combine(_paths.Models, "tiny")));
        Assert.False(Directory.Exists(cache));
        Assert.True(logger.Saw(EdgeAiEventIds.OrtCachePurged));
        Assert.Empty(store.ProvisionedModelIds);
    }

    [Fact]
    public async Task ReprovisioningAtANewShaDeletesTheWholeStaleModelIdShaPair()
    {
        var logger = new RecordingLogger<OnnxModelStore>();
        using var store = Store(new CountingSource(_paths), logger);

        // The directory a previous revision of the same model id left behind.
        var staleDirectory = Path.Combine(_paths.Models, "tiny", Sha(VocabBytes)[..16]);
        Directory.CreateDirectory(staleDirectory);
        await File.WriteAllTextAsync(Path.Combine(staleDirectory, "leftover.bin"), "x", TestContext.Current.CancellationToken);

        var staleCache = Path.Combine(_paths.OrtCache, "tiny", Sha(VocabBytes)[..16]);
        Directory.CreateDirectory(staleCache);
        await File.WriteAllTextAsync(Path.Combine(staleCache, "compiled.mlmodelc"), "x", TestContext.Current.CancellationToken);

        Assert.NotEqual(staleDirectory, Path.Combine(_paths.Models, "tiny", Sha(GraphBytes)[..16]));

        await store.EnsureAsync(Manifest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(staleDirectory));
        Assert.False(Directory.Exists(staleCache));
        Assert.True(logger.Saw(EdgeAiEventIds.OrtCachePurged));
    }

    [Fact]
    public async Task NoSourceThatCanProvideIsModelNotProvisioned()
    {
        using var store = new OnnxModelStore(_paths, [], NullLogger<OnnxModelStore>.Instance);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => store.EnsureAsync(Manifest(), cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelNotProvisioned, ex.Code);
        Assert.Equal("tiny", ex.ModelId);
    }

    [Fact]
    public async Task TheFileSourceIsOrderedLastWhateverItsRegistrationIndex()
    {
        var manifest = Manifest();

        // Stage the files where the file source will find them, so BOTH sources can provide and the
        // only thing deciding the outcome is the probe order.
        var staging = Path.Combine(_paths.Models, "tiny");
        Directory.CreateDirectory(Path.Combine(staging, "onnx"));
        await File.WriteAllBytesAsync(
            Path.Combine(staging, "onnx", "model.onnx"), GraphBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(staging, "vocab.txt"), VocabBytes, TestContext.Current.CancellationToken);

        var file = new FileOnnxModelSource(_paths);
        var counting = new CountingSource(_paths);

        // The file source is registered FIRST, exactly as AddOnnx registers it, and is still the
        // fallback: the store moves it to the end.
        using var store = new OnnxModelStore(_paths, [file, counting], NullLogger<OnnxModelStore>.Instance);

        var provisioned = await store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ModelProvisioningSource.Bundled, provisioned.Source);
        Assert.Equal(1, counting.Calls);
    }

    public void Dispose()
    {
        _paths.Dispose();
        GC.SuppressFinalize(this);
    }
}
