using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// The <see cref="HttpOnnxModelSourceOptions"/> defaults that change behaviour, and the one place a
/// model URL is composed. Two of these defaults are the difference between "downloads a model" and
/// "throws".
/// </summary>
public class ModelUrlTests : IDisposable
{
    private const string MiniLmRevision = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41";

    private readonly TempModelPaths _paths = new();

    /// <summary>The shipped int8 preset's manifest, as Task 3.3's constants declare it.</summary>
    private static OnnxModelManifest MiniLmL6V2Int8() => new()
    {
        ModelId = "MiniLmL6V2Int8",
        GraphFile = "onnx/model_qint8_arm64.onnx",
        SpdxLicense = "Apache-2.0",
        HuggingFaceRepo = "sentence-transformers/all-MiniLM-L6-v2",
        HuggingFaceRevision = MiniLmRevision,
        Files =
        [
            new OnnxModelFile(
                "onnx/model_qint8_arm64.onnx",
                OnnxModelFileRole.Graph,
                23_000_000,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("graph")))),
        ],
    };

    [Fact]
    public void TheDefaultsComposeTheHuggingFaceResolveUrlForTheInt8Preset()
    {
        var options = new HttpOnnxModelSourceOptions();

        var uri = HttpOnnxModelSource.ComposeUri(options, MiniLmL6V2Int8(), "onnx/model_qint8_arm64.onnx");

        Assert.Equal(
            "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/" +
            MiniLmRevision + "/onnx/model_qint8_arm64.onnx",
            uri.ToString());
    }

    [Fact]
    public void ACustomBaseAddressAndTemplateAreHonouredRatherThanHardCodedPast()
    {
        var options = new HttpOnnxModelSourceOptions
        {
            BaseAddress = new Uri("https://models.corp.example/mirror/"),

            // Deliberately reordered and reshaped: the three placeholders are positional-BY-NAME.
            UrlTemplate = "{revision}/{repo}/blob/{path}",
        };

        var uri = HttpOnnxModelSource.ComposeUri(options, MiniLmL6V2Int8(), "vocab.txt");

        Assert.Equal(
            "https://models.corp.example/mirror/" + MiniLmRevision +
            "/sentence-transformers/all-MiniLM-L6-v2/blob/vocab.txt",
            uri.ToString());
    }

    [Fact]
    public void TheRevisionIsThePinnedCommitShaAndNeverMain()
    {
        var manifest = MiniLmL6V2Int8();

        Assert.Equal(40, manifest.HuggingFaceRevision!.Length);
        Assert.DoesNotContain("main", manifest.HuggingFaceRevision, StringComparison.Ordinal);

        var uri = HttpOnnxModelSource.ComposeUri(new HttpOnnxModelSourceOptions(), manifest, "onnx/model_qint8_arm64.onnx");
        Assert.Contains("/resolve/" + MiniLmRevision + "/", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsThatChangeBehaviourAreTheOnesSpecSixThreeDeclares()
    {
        var options = new HttpOnnxModelSourceOptions();

        Assert.Equal(new Uri("https://huggingface.co/"), options.BaseAddress);
        Assert.Equal("{repo}/resolve/{revision}/{path}", options.UrlTemplate);
        Assert.Equal(4, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(10), options.BodyTimeout);
        Assert.Equal(32L * 1024 * 1024, options.FreeDiskMarginBytes);
        Assert.Null(options.BearerToken);

        // The two that decide whether anything is fetched at all.
        Assert.True(options.AllowDownload);
    }

    [Fact]
    public async Task AllowDownloadFalseRaisesModelNotProvisionedWithoutOpeningASocket()
    {
        using var handler = new RefusingHandler();
        var source = new HttpOnnxModelSource(
            new HttpClient(handler),
            _paths,
            Options.Create(new HttpOnnxModelSourceOptions { AllowDownload = false }),
            NullLogger<HttpOnnxModelSource>.Instance);

        var ex = await Assert.ThrowsAsync<EdgeModelProvisioningException>(
            () => source.EnsureAsync(MiniLmL6V2Int8(), progress: null, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelNotProvisioned, ex.Code);
        Assert.Equal("MiniLmL6V2Int8", ex.ModelId);
        Assert.Equal(0, handler.Sends);

        // It still says where it would have gone, which is the first thing anyone asks.
        Assert.NotNull(ex.SourceUri);
    }

    /// <summary>Fails the test rather than the request if anything is ever sent.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sends);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    public void Dispose()
    {
        _paths.Dispose();
        GC.SuppressFinalize(this);
    }
}
