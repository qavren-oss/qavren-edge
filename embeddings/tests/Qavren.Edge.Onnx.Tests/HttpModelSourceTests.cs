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

    public void Dispose()
    {
        _paths.Dispose();
        GC.SuppressFinalize(this);
    }
}
