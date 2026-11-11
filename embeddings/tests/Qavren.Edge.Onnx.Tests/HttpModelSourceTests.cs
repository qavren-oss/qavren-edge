using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qavren.Edge.Onnx.Internal;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// Spec 10.2's download, against a loopback handler. Nothing here touches a network: the only
/// interesting behaviours - resume, restart, digest, disk, retry - are all decided from response
/// headers and status codes.
/// </summary>
public class HttpModelSourceTests : IDisposable
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");

    private readonly TempModelPaths _paths = new();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static OnnxModelManifest Manifest() => new()
    {
        ModelId = "tiny",
        GraphFile = "onnx/model.onnx",
        SpdxLicense = "Apache-2.0",
        HuggingFaceRepo = "acme/tiny",
        HuggingFaceRevision = "0123456789abcdef0123456789abcdef01234567",
        Files = [new OnnxModelFile("onnx/model.onnx", OnnxModelFileRole.Graph, Body.Length, Sha(Body))],
    };

    /// <summary>Answers each request from a queue of responders, recording what it was asked.</summary>
    private sealed class QueuedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
        : HttpMessageHandler
    {
        private int _index;

        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public int Count => Volatile.Read(ref _index);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var index = Interlocked.Increment(ref _index) - 1;
            var responder = responders[Math.Min(index, responders.Length - 1)];
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] content, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static HttpResponseMessage Partial(byte[] content, long from, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(content),
        };

        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, total - 1, total);
        return response;
    }

    /// <summary>Reports on the calling thread, so an assertion can actually see the reports.</summary>
    private sealed class SyncProgress : IProgress<ModelProvisioningProgress>
    {
        public List<ModelProvisioningProgress> Reports { get; } = [];

        public void Report(ModelProvisioningProgress value) => Reports.Add(value);
    }

    private HttpOnnxModelSource Source(
        QueuedHandler handler,
        Action<HttpOnnxModelSourceOptions>? configure = null,
        ILogger<HttpOnnxModelSource>? logger = null)
    {
        var options = new HttpOnnxModelSourceOptions();
        configure?.Invoke(options);

        return new HttpOnnxModelSource(
            new HttpClient(handler),
            _paths,
            Options.Create(options),
            logger ?? NullLogger<HttpOnnxModelSource>.Instance)
        {
            // Never a real disk and never a real wait.
            FreeSpaceProvider = _ => 1024L * 1024 * 1024,
            DelayAsync = static (_, _) => Task.CompletedTask,
        };
    }

    private string PartPath()
    {
        var directory = OnnxModelLayout.DirectoryFor(_paths.Models, Manifest());
        Directory.CreateDirectory(Path.Combine(directory, "onnx"));
        return OnnxModelLayout.FilePath(directory, "onnx/model.onnx") + ".part";
    }

    [Fact]
    public async Task A206ResumesFromThePartialFileAndReportsResumedProgress()
    {
        const int Prefix = 12;
        await File.WriteAllBytesAsync(PartPath(), Body[..Prefix], TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => Partial(Body[Prefix..], Prefix, Body.Length));
        var source = Source(handler);

        var progress = new SyncProgress();

        var provisioned = await source.EnsureAsync(Manifest(), progress, TestContext.Current.CancellationToken);

        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(PartPath()));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Prefix, request.Headers.Range?.Ranges.Single().From);

        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, r => Assert.True(r.Resumed));

        // The resumed prefix counts: a bar that restarted at 0 would be lying about the transfer.
        var last = progress.Reports[^1];
        Assert.Equal(Body.Length, last.BytesCompleted);
        Assert.Equal(Body.Length, last.BytesTotal);
    }

    [Fact]
    public async Task A200ToARangedRequestTruncatesThePartFileAndLogs643()
    {
        // Bytes that are NOT a prefix of the body: appending the full 200 body to them would produce
        // a file that is longer than the manifest declares and hashes to nothing.
        await File.WriteAllBytesAsync(PartPath(), Encoding.UTF8.GetBytes("GARBAGE!"), TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => Ok(Body));
        var logger = new RecordingLogger<HttpOnnxModelSource>();
        var source = Source(handler, logger: logger);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        var written = await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken);
        Assert.Equal(Body, written);
        Assert.Equal(Body.Length, written.Length);
        Assert.True(logger.Saw(EdgeAiEventIds.ModelDownloadRestarted));
    }

    [Fact]
    public async Task ADigestMismatchDeletesTheFileRetriesOnceAndThenThrowsWithBothDigests()
    {
        var wrong = Encoding.UTF8.GetBytes("not the model bytes at all, no sir");
        using var handler = new QueuedHandler(_ => Ok(wrong));
        var logger = new RecordingLogger<HttpOnnxModelSource>();
        var source = Source(handler, logger: logger);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelHashMismatch, ex.Code);
        Assert.Equal(Sha(Body), ex.ExpectedSha256);
        Assert.Equal(Sha(wrong), ex.ActualSha256);
        Assert.Equal("onnx/model.onnx", ex.RelativePath);

        // One retry, and one only: the same wrong bytes twice is a wrong manifest, not a wire error.
        Assert.Equal(2, handler.Count);
        Assert.False(File.Exists(PartPath()));
        Assert.True(logger.Saw(EdgeAiEventIds.ModelHashMismatch));
    }

    [Fact]
    public async Task AShortVolumeIsRefusedBeforeAnythingIsWritten()
    {
        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler);
        source.FreeSpaceProvider = _ => 1024;

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelInsufficientDiskSpace, ex.Code);
        Assert.False(File.Exists(PartPath()));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task A503IsRetriedAndTheNextAttemptSucceeds()
    {
        using var handler = new QueuedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.RequestTimeout),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ => Ok(Body));

        var source = Source(handler);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.Count);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FourFailedAttemptsIsModelDownloadFailed()
    {
        using var handler = new QueuedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var logger = new RecordingLogger<HttpOnnxModelSource>();
        var source = Source(handler, logger: logger);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelDownloadFailed, ex.Code);
        Assert.Equal(4, handler.Count);
        Assert.NotNull(ex.SourceUri);
        Assert.True(logger.Saw(EdgeAiEventIds.ModelDownloadFailed));
    }

    [Fact]
    public async Task A404IsNotRetried()
    {
        using var handler = new QueuedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = Source(handler);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelDownloadFailed, ex.Code);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task AllowDownloadFalseIsModelNotProvisionedAndOpensNoSocket()
    {
        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler, o => o.AllowDownload = false);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelNotProvisioned, ex.Code);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ABearerTokenIsSentWhenOneIsConfigured()
    {
        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler, o => o.BearerToken = "hf_secret");

        await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("hf_secret", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void AManifestWithNoRepoOrRevisionCannotBeProvidedOverHttp()
    {
        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler);

        Assert.True(source.CanProvide(Manifest()));
        Assert.False(source.CanProvide(Manifest() with { HuggingFaceRepo = null }));
        Assert.False(source.CanProvide(Manifest() with { HuggingFaceRevision = null }));
    }

    private static readonly byte[] Vocab = Encoding.UTF8.GetBytes("hello\nworld\n");

    /// <summary>The graph first and a second file after it, the order a real bundle downloads in.</summary>
    private static OnnxModelManifest TwoFileManifest() => Manifest() with
    {
        Files =
        [
            new OnnxModelFile("onnx/model.onnx", OnnxModelFileRole.Graph, Body.Length, Sha(Body)),
            new OnnxModelFile("vocab.txt", OnnxModelFileRole.Vocabulary, Vocab.Length, Sha(Vocab)),
        ],
    };

    private string DestinationPath(string relativePath = "onnx/model.onnx")
    {
        var directory = OnnxModelLayout.DirectoryFor(_paths.Models, Manifest());
        Directory.CreateDirectory(Path.Combine(directory, "onnx"));
        return OnnxModelLayout.FilePath(directory, relativePath);
    }

    /// <summary>
    /// A body handed out a chunk at a time with a pause before each one, and no declared length. An
    /// infinite pause is a body that has stalled.
    /// </summary>
    private sealed class TrickleContent(byte[] content, int chunk, TimeSpan gap) : HttpContent
    {
        private long _served;

        public long BytesServed => Interlocked.Read(ref _served);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new TrickleStream(this));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class TrickleStream(TrickleContent owner) : Stream
        {
            private int _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(owner.Gap, cancellationToken).ConfigureAwait(false);

                var take = Math.Min(Math.Min(buffer.Length, owner.Chunk), owner.Content.Length - _position);
                if (take <= 0)
                {
                    return 0;
                }

                owner.Content.AsMemory(_position, take).CopyTo(buffer);
                _position += take;
                Interlocked.Add(ref owner._served, take);
                return take;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private byte[] Content => content;

        private int Chunk => chunk;

        private TimeSpan Gap => gap;
    }

    [Fact]
    public async Task ACompletePartFileIsVerifiedAndRenamedWithoutAskingTheServerForAnything()
    {
        // What a cancel, or a kill, during the post-download hash leaves behind. "Range: bytes=36-"
        // for a 36-byte file is a 416, which is not retryable - so before this was handled the model
        // could never be provisioned on that device again.
        await File.WriteAllBytesAsync(PartPath(), Body, TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        var source = Source(handler);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.Count);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(PartPath()));
    }

    [Fact]
    public async Task ACompletePartFileWithTheWrongBytesIsDeletedAndTheFileIsFetchedFromZero()
    {
        var wrong = Encoding.UTF8.GetBytes(new string('x', Body.Length));
        await File.WriteAllBytesAsync(PartPath(), wrong, TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Headers.Range);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APartFileLongerThanTheManifestDeclaresIsDiscardedRatherThanResumed()
    {
        await File.WriteAllBytesAsync(
            PartPath(), Encoding.UTF8.GetBytes(new string('x', Body.Length + 5)), TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Headers.Range);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A416ToAResumeDiscardsThePartFileAndTheNextAttemptStartsOver()
    {
        await File.WriteAllBytesAsync(PartPath(), Body[..12], TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable),
            _ => Ok(Body));
        var source = Source(handler);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Count);
        Assert.NotNull(handler.Requests.First().Headers.Range);
        Assert.Null(handler.Requests.Last().Headers.Range);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFileAlreadyInPlaceAndCorrectIsNotFetchedAgain()
    {
        // The interrupted bundle: the first file made it, a later one did not. In the shipped chat
        // presets the first two files are 1.2 GB and the one most likely to fail is 17 MB.
        await File.WriteAllBytesAsync(DestinationPath(), Body, TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => Ok(Vocab));
        var source = Source(handler);
        var progress = new SyncProgress();

        var provisioned = await source.EnsureAsync(TwoFileManifest(), progress, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/vocab.txt", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(Sha(Body), provisioned.GraphSha256);
        Assert.Equal(Vocab, await File.ReadAllBytesAsync(provisioned.Files["vocab.txt"], TestContext.Current.CancellationToken));

        // The bar still reaches 100% for the file nothing was transferred for.
        Assert.Contains(progress.Reports, r => r.RelativePath == "onnx/model.onnx" && r.BytesCompleted == Body.Length);
    }

    [Fact]
    public async Task AFileInPlaceAtTheRightLengthWithTheWrongBytesIsFetchedNotTrusted()
    {
        await File.WriteAllBytesAsync(
            DestinationPath(), Encoding.UTF8.GetBytes(new string('x', Body.Length)), TestContext.Current.CancellationToken);

        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler);

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Count);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABodyLongerThanTheManifestDeclaresIsStoppedBeforeItIsWrittenOut()
    {
        // 4 MiB against a 36-byte manifest entry, with no Content-Length to give it away. The digest
        // would refuse it too - after every byte had been written to the device.
        using var endless = new TrickleContent(new byte[4 * 1024 * 1024], chunk: 64 * 1024, gap: TimeSpan.Zero);
        using var handler = new QueuedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = endless });
        var source = Source(handler, o => o.MaxAttempts = 1);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelHashMismatch, ex.Code);
        Assert.Equal(Body.Length, ex.ExpectedBytes);
        Assert.False(File.Exists(PartPath()));
        Assert.False(File.Exists(DestinationPath()));

        // One read past the limit, not four megabytes.
        Assert.InRange(endless.BytesServed, 1, 128 * 1024);
    }

    [Fact]
    public async Task ASlowBodyThatKeepsMovingOutlivesTheBodyTimeout()
    {
        // Four chunks 400 ms apart is 1.6 s of body against a 1 s timeout. As a deadline that fails
        // every attempt a slow link is given; as a stall timeout it is one clean attempt.
        using var slow = new TrickleContent(Body, chunk: Body.Length / 4, gap: TimeSpan.FromMilliseconds(400));
        using var handler = new QueuedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = slow });
        var source = Source(handler, o =>
        {
            o.BodyTimeout = TimeSpan.FromSeconds(1);
            o.MaxAttempts = 1;
        });

        var provisioned = await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Count);
        Assert.Equal(Body, await File.ReadAllBytesAsync(provisioned.GraphPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABodyThatStallsIsStillCancelledByTheBodyTimeout()
    {
        using var stalled = new TrickleContent(Body, chunk: Body.Length, gap: Timeout.InfiniteTimeSpan);
        using var handler = new QueuedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled });
        var source = Source(handler, o =>
        {
            o.BodyTimeout = TimeSpan.FromMilliseconds(100);
            o.MaxAttempts = 1;
        });

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        // The timeout, not the caller: it is a failed attempt, not a cancellation.
        Assert.Equal(EdgeErrorCode.ModelDownloadFailed, ex.Code);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task ADigestMismatchOnTheLastPermittedAttemptIsStillADigestMismatch()
    {
        var wrong = Encoding.UTF8.GetBytes(new string('x', Body.Length));
        using var handler = new QueuedHandler(_ => Ok(wrong));
        var source = Source(handler, o => o.MaxAttempts = 1);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        // Not ModelDownloadFailed with the digests buried in an InnerException.
        Assert.Equal(EdgeErrorCode.ModelHashMismatch, ex.Code);
        Assert.Equal(Sha(wrong), ex.ActualSha256);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ABearerTokenIsNeverSentInClear()
    {
        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler, o =>
        {
            o.BaseAddress = new Uri("http://models.corp.example/");
            o.BearerToken = "hf_secret";
        });

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelDownloadFailed, ex.Code);
        Assert.Equal(0, handler.Count);
        Assert.DoesNotContain("hf_secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABearerTokenIsStillSentToALoopbackMirrorOverHttp()
    {
        using var handler = new QueuedHandler(_ => Ok(Body));
        var source = Source(handler, o =>
        {
            o.BaseAddress = new Uri("http://localhost:8080/");
            o.BearerToken = "hf_secret";
        });

        await source.EnsureAsync(Manifest(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("hf_secret", Assert.Single(handler.Requests).Headers.Authorization?.Parameter);
    }

    [Fact]
    public void TheDefaultFreeSpaceProbeReadsTheVolumeOfTheDirectoryEvenBeforeItExists()
    {
        // Every other test replaces the probe, so this is the only place the real one runs. It used
        // to read Path.GetPathRoot of the directory, which is "/" for every path on every Unix: the
        // read-only system image on Android, not the partition the models are written to.
        var existing = _paths.Models;
        var missing = Path.Combine(existing, "not", "created", "yet");

        var whole = OnnxModelLayout.FreeSpace(existing);
        var nested = OnnxModelLayout.FreeSpace(missing);

        Assert.NotNull(whole);
        Assert.NotNull(nested);
        Assert.True(whole > 0);

        // The same volume, read twice a moment apart while other tests write to it.
        Assert.InRange(Math.Abs(whole.Value - nested.Value), 0, 512L * 1024 * 1024);
        Assert.InRange(
            Math.Abs(whole.Value - new DriveInfo(existing).AvailableFreeSpace), 0, 512L * 1024 * 1024);
    }

    public void Dispose()
    {
        _paths.Dispose();
        GC.SuppressFinalize(this);
    }
}
