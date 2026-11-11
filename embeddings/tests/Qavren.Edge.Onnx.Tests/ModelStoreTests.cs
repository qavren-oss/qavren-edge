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

    /// <summary>A bundled source over two in-memory assets, either of which a test can swap.</summary>
    private BundledOnnxModelSource Bundled(byte[]? graph = null, byte[]? vocab = null) =>
        new(_paths, (path, _) => ValueTask.FromResult<Stream>(new MemoryStream(
            path == "onnx/model.onnx" ? graph ?? GraphBytes : vocab ?? VocabBytes)));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("tiny/../..")]
    public async Task RemoveRefusesAnIdThatDoesNotStayInsideTheModelRoot(string modelId)
    {
        using var store = Store(new CountingSource(_paths));
        await store.EnsureAsync(Manifest(), cancellationToken: TestContext.Current.CancellationToken);

        // ".." is the directory that holds the model root AND the ORT cache; on a desktop host it
        // is the app's data directory, database included. "." is the model root itself.
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RemoveAsync(modelId, TestContext.Current.CancellationToken).AsTask());

        Assert.True(Directory.Exists(_paths.Models));
        Assert.True(Directory.Exists(_paths.OrtCache));
        Assert.True(store.IsProvisioned(Manifest()));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("onnx/../../../escape.bin")]
    public async Task AManifestPathThatClimbsOutOfTheModelDirectoryIsRefusedBeforeAnythingIsWritten(string relativePath)
        => await AssertManifestPathRefusedAsync(relativePath);

    [Fact]
    public async Task ARootedManifestPathIsRefusedBeforeAnythingIsWritten()
        => await AssertManifestPathRefusedAsync(Path.Combine(Path.GetTempPath(), "escape.bin").Replace('\\', '/'));

    [Fact]
    public async Task FilesStagedInASubdirectoryOfTheModelIdDirectoryAreProvisionedNotPurgedAsAStaleRevision()
    {
        // <Models>/<modelId>/ is the file source's first candidate, and it is also where the stale
        // revisions live. "onnx" is not one of those: only sixteen hex characters are.
        var staging = Path.Combine(_paths.Models, "tiny");
        Directory.CreateDirectory(Path.Combine(staging, "onnx"));
        var stagedGraph = Path.Combine(staging, "onnx", "model.onnx");
        await File.WriteAllBytesAsync(stagedGraph, GraphBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(staging, "vocab.txt"), VocabBytes, TestContext.Current.CancellationToken);

        using var store = Store(new FileOnnxModelSource(_paths));

        var provisioned = await store.EnsureAsync(Manifest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ModelProvisioningSource.File, provisioned.Source);
        Assert.Equal(GraphBytes, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(stagedGraph));
    }

    private async Task AssertManifestPathRefusedAsync(string relativePath)
    {
        var manifest = Manifest() with
        {
            Files =
            [
                new OnnxModelFile("onnx/model.onnx", OnnxModelFileRole.Graph, GraphBytes.Length, Sha(GraphBytes)),
                new OnnxModelFile(relativePath, OnnxModelFileRole.Auxiliary, VocabBytes.Length, Sha(VocabBytes)),
            ],
        };

        using var store = Store(Bundled());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken).AsTask())
            .ConfigureAwait(true);

        Assert.Empty(Directory.EnumerateFiles(_paths.Models, "escape.bin*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_paths.Models)!, "escape.bin*"));
    }

    [Fact]
    public async Task ABundledAssetIsStagedVerifiedAndOnlyThenRenamedIntoPlace()
    {
        using var store = Store(Bundled());

        var provisioned = await store.EnsureAsync(Manifest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GraphBytes, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
        Assert.Equal(VocabBytes, await File.ReadAllBytesAsync(provisioned.Files["vocab.txt"], TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(provisioned.Directory, "*" + OnnxModelLayout.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AFailedReprovisionLeavesNoMarkerAndNoUnverifiedFileForTheFastPathToTrust()
    {
        var manifest = Manifest();

        string vocabPath;
        using (var first = Store(Bundled()))
        {
            vocabPath = (await first.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken))
                .Files["vocab.txt"];
        }

        // Something truncates one file, so the next launch has to re-provision...
        await File.WriteAllBytesAsync(vocabPath, [1, 2, 3], TestContext.Current.CancellationToken);

        // ...and this time the asset is the right LENGTH and the wrong bytes. Written straight to
        // the destination under the surviving marker, that is exactly what the length-only fast path
        // accepts on the launch after this one.
        var wrongVocab = Encoding.UTF8.GetBytes(new string('x', VocabBytes.Length));
        using (var second = Store(Bundled(vocab: wrongVocab)))
        {
            var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
                () => second.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(EdgeErrorCode.ModelHashMismatch, ex.Code);
        }

        var directory = Path.GetDirectoryName(vocabPath)!;
        Assert.False(File.Exists(Path.Combine(directory, OnnxModelLayout.MarkerFileName)));
        Assert.False(File.Exists(OnnxModelLayout.PartPath(vocabPath)));
        Assert.NotEqual(wrongVocab, await File.ReadAllBytesAsync(vocabPath, TestContext.Current.CancellationToken));

        using var third = Store(Bundled());
        Assert.False(third.IsProvisioned(manifest));
    }

    [Fact]
    public async Task AFileSourceVerifiesItsCopyBeforeItReachesTheDestinationAndLeavesTheStagedOriginalAlone()
    {
        var manifest = Manifest();

        // Staged where the file source looks first, with a vocabulary of the right length and the
        // wrong bytes.
        var staging = Path.Combine(_paths.Models, "tiny");
        Directory.CreateDirectory(Path.Combine(staging, "onnx"));
        var stagedVocab = Path.Combine(staging, "vocab.txt");
        var wrongVocab = Encoding.UTF8.GetBytes(new string('x', VocabBytes.Length));
        await File.WriteAllBytesAsync(Path.Combine(staging, "onnx", "model.onnx"), GraphBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(stagedVocab, wrongVocab, TestContext.Current.CancellationToken);

        using var store = Store(new FileOnnxModelSource(_paths));

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelHashMismatch, ex.Code);

        // The copy is gone; the consumer's own file is not the store's to delete.
        var destination = OnnxModelLayout.FilePath(OnnxModelLayout.DirectoryFor(_paths.Models, manifest), "vocab.txt");
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(OnnxModelLayout.PartPath(destination)));
        Assert.Equal(wrongVocab, await File.ReadAllBytesAsync(stagedVocab, TestContext.Current.CancellationToken));
    }

    /// <summary>Holds provisioning open until the test lets it go.</summary>
    private sealed class GatedSource(IOnnxModelSource inner) : IOnnxModelSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "gated";

        public bool CanProvide(OnnxModelManifest manifest) => true;

        public async ValueTask<ProvisionedModel> EnsureAsync(
            OnnxModelManifest manifest,
            IProgress<ModelProvisioningProgress>? progress,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.EnsureAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task RemoveWaitsForAProvisioningInFlightRatherThanDeletingUnderIt()
    {
        var manifest = Manifest();
        var source = new GatedSource(new CountingSource(_paths));
        using var store = Store(source);

        var ensure = store.EnsureAsync(manifest, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var remove = store.RemoveAsync("tiny", TestContext.Current.CancellationToken).AsTask();

        // Queued behind the provisioning, not racing it.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(remove.IsCompleted);

        source.Release.SetResult();
        var provisioned = await ensure.ConfigureAwait(true);
        await remove.ConfigureAwait(true);

        Assert.False(Directory.Exists(provisioned.Directory));
        Assert.Empty(store.ProvisionedModelIds);
    }

    [Fact]
    public void BytesOnDiskCountsAPlacedFileOrItsStagingFileAndNothingElse()
    {
        var file = new OnnxModelFile("vocab.txt", OnnxModelFileRole.Vocabulary, VocabBytes.Length, Sha(VocabBytes));
        var directory = Path.Combine(_paths.Models, "layout");
        Directory.CreateDirectory(directory);
        var destination = OnnxModelLayout.FilePath(directory, "vocab.txt");

        Assert.Equal(0, OnnxModelLayout.BytesOnDisk(directory, file));

        // What a ranged GET resumes from.
        File.WriteAllBytes(OnnxModelLayout.PartPath(destination), VocabBytes[..5]);
        Assert.Equal(5, OnnxModelLayout.BytesOnDisk(directory, file));

        // A file in place at the wrong length is one the downloader deletes, so it counts for nothing.
        File.WriteAllBytes(destination, [1, 2, 3]);
        Assert.Equal(5, OnnxModelLayout.BytesOnDisk(directory, file));

        File.WriteAllBytes(destination, VocabBytes);
        Assert.Equal(VocabBytes.Length, OnnxModelLayout.BytesOnDisk(directory, file));
    }

    public void Dispose()
    {
        _paths.Dispose();
        GC.SuppressFinalize(this);
    }
}
