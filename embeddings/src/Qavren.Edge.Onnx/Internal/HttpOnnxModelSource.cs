using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx;

/// <summary>
/// How <see cref="HttpOnnxModelSource"/> fetches. <see cref="BaseAddress"/> and
/// <see cref="UrlTemplate"/> together are the ONLY place a Hugging Face URL is constructed -
/// nothing else in this suite concatenates one - so a consumer pointing at a mirror or a corporate
/// proxy changes exactly these two properties.
/// </summary>
public sealed class HttpOnnxModelSourceOptions
{
    /// <summary>The host every download is resolved against.</summary>
    public Uri BaseAddress { get; set; } = new("https://huggingface.co/");

    /// <summary>
    /// <c>{repo}/resolve/{revision}/{path}</c>. The three placeholders are positional-by-name, and
    /// <c>{revision}</c> is filled from the manifest's pinned commit SHA, never <c>"main"</c>.
    /// </summary>
    public string UrlTemplate { get; set; } = "{repo}/resolve/{revision}/{path}";

    /// <summary>Total attempts per file, including the first. Backoff is exponential with jitter.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// The longest a response body may go without delivering a byte. <c>HttpClient.Timeout</c>
    /// stops applying once the response headers are read, so without this a stalled body hangs
    /// forever. The clock restarts every time bytes arrive: it bounds a stall, not the transfer, so
    /// a slow link that keeps moving finishes a gigabyte-sized file in one attempt.
    /// </summary>
    public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Headroom kept free on the volume, on top of the bytes still to fetch.</summary>
    public long FreeDiskMarginBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    /// A Hub token, for a gated repo. Sent only over HTTPS or to a loopback address: with any other
    /// <see cref="BaseAddress"/> the download is refused rather than the token put on the wire in
    /// clear.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// False turns a missing model into <see cref="EdgeErrorCode.ModelNotProvisioned"/> instead of
    /// a download - the switch an app flips on cellular.
    /// </summary>
    public bool AllowDownload { get; set; } = true;
}

/// <summary>
/// Resumable, verified HTTP provisioning. Foreground-only by design: an iOS download that survives
/// backgrounding needs <c>NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration</c>, which
/// <see cref="HttpClient"/> does not map onto.
/// </summary>
public sealed class HttpOnnxModelSource : IOnnxModelSource
{
    private static readonly Action<ILogger, string, string, Uri, Exception?> s_started =
        LoggerMessage.Define<string, string, Uri>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.ModelDownloadStarted, nameof(EdgeAiEventIds.ModelDownloadStarted)),
            "Downloading {ModelId}/{RelativePath} from {Uri}.");

    private static readonly Action<ILogger, string, string, long, Exception?> s_resumed =
        LoggerMessage.Define<string, string, long>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.ModelDownloadResumed, nameof(EdgeAiEventIds.ModelDownloadResumed)),
            "Resuming {ModelId}/{RelativePath} from byte {Offset}; the server answered 206.");

    private static readonly Action<ILogger, string, string, long, Exception?> s_restarted =
        LoggerMessage.Define<string, string, long>(
            LogLevel.Warning,
            new EventId(EdgeAiEventIds.ModelDownloadRestarted, nameof(EdgeAiEventIds.ModelDownloadRestarted)),
            "The server answered 200 to a ranged request for {ModelId}/{RelativePath}; the partial file " +
            "of {Offset} bytes was truncated rather than appended to, because appending a full body to a " +
            "partial file silently corrupts it.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> s_hashMismatch =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Error,
            new EventId(EdgeAiEventIds.ModelHashMismatch, nameof(EdgeAiEventIds.ModelHashMismatch)),
            "{ModelId}/{RelativePath} hashed to {Actual}, not {Expected}. The file was deleted.");

    private static readonly Action<ILogger, string, string, int, Exception?> s_downloadFailed =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Error,
            new EventId(EdgeAiEventIds.ModelDownloadFailed, nameof(EdgeAiEventIds.ModelDownloadFailed)),
            "{ModelId}/{RelativePath} failed after {Attempts} attempts.");

    private static readonly TimeSpan s_progressInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _httpClient;
    private readonly IEdgeModelPaths _paths;
    private readonly IOptions<HttpOnnxModelSourceOptions> _options;
    private readonly ILogger<HttpOnnxModelSource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClient">The client. Not owned: whoever registered it disposes it.</param>
    /// <param name="paths">The model root.</param>
    /// <param name="options">The fetch options.</param>
    /// <param name="logger">The logger.</param>
    public HttpOnnxModelSource(
        HttpClient httpClient,
        IEdgeModelPaths paths,
        IOptions<HttpOnnxModelSourceOptions> options,
        ILogger<HttpOnnxModelSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _paths = paths;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "http";

    /// <summary>
    /// Free bytes on the volume holding a directory, or null for "unknown". Replaced by tests, which
    /// is the only way to assert the refusal without filling a real disk.
    /// </summary>
    internal Func<string, long?> FreeSpaceProvider { get; set; } = OnnxModelLayout.FreeSpace;

    /// <summary>
    /// The backoff sleep. Replaced by tests so the retry ladder is asserted in milliseconds rather
    /// than in seconds of real waiting.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <inheritdoc />
    /// <remarks>A repo and a pinned revision are both required: there is no URL without them.</remarks>
    public bool CanProvide(OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return !string.IsNullOrWhiteSpace(manifest.HuggingFaceRepo) &&
               !string.IsNullOrWhiteSpace(manifest.HuggingFaceRevision);
    }

    /// <summary>
    /// The one place a model URL is composed. <c>{repo}</c>, <c>{revision}</c> and <c>{path}</c> are
    /// substituted by name, so a reordered template still composes correctly.
    /// </summary>
    /// <param name="options">The fetch options.</param>
    /// <param name="manifest">The manifest, which carries the repo and the pinned revision.</param>
    /// <param name="relativePath">The manifest-relative file path.</param>
    /// <returns>The absolute URL.</returns>
    internal static Uri ComposeUri(
        HttpOnnxModelSourceOptions options,
        OnnxModelManifest manifest,
        string relativePath)
    {
        var relative = options.UrlTemplate
            .Replace("{repo}", manifest.HuggingFaceRepo, StringComparison.Ordinal)
            .Replace("{revision}", manifest.HuggingFaceRevision, StringComparison.Ordinal)
            .Replace("{path}", relativePath, StringComparison.Ordinal);

        // RFC 3986 resolution replaces the last segment of a base that does not end in '/', so
        // "https://models.corp.example/mirror" would lose "mirror" and every request would be a
        // 404 against the host's root. A base address is a prefix here, with or without the slash.
        var baseAddress = options.BaseAddress;
        if (!baseAddress.AbsolutePath.EndsWith('/'))
        {
            baseAddress = new UriBuilder(baseAddress) { Path = baseAddress.AbsolutePath + "/" }.Uri;
        }

        return new Uri(baseAddress, relative);
    }

    /// <inheritdoc />
    public async ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var options = _options.Value;
        var started = Stopwatch.GetTimestamp();
        var directory = OnnxModelLayout.DirectoryFor(_paths.Models, manifest);

        if (!options.AllowDownload)
        {
            // Never opens a socket: this is the switch an app flips on cellular, and the whole point
            // of it is that nothing is transferred.
            throw new EdgeModelProvisioningException(
                EdgeErrorCode.ModelNotProvisioned,
                manifest.ModelId,
                manifest.GraphFile,
                $"'{manifest.ModelId}' is not on disk and HttpOnnxModelSourceOptions.AllowDownload is false, " +
                "so nothing was fetched.")
            {
                SourceUri = ComposeUri(options, manifest, manifest.GraphFile),
            };
        }

        RefuseATokenInClear(options, manifest);

        Directory.CreateDirectory(directory);
        var graphSha = OnnxModelLayout.Graph(manifest).Sha256;

        foreach (var file in manifest.Files)
        {
            var actual = await DownloadAsync(options, manifest, file, directory, progress, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(file.RelativePath, manifest.GraphFile, StringComparison.Ordinal))
            {
                graphSha = actual;
            }
        }

        return new ProvisionedModel(
            manifest.ModelId,
            directory,
            OnnxModelLayout.FilePath(directory, manifest.GraphFile),
            graphSha,
            OnnxModelLayout.Resolve(directory, manifest),
            ModelProvisioningSource.Downloaded,
            Stopwatch.GetElapsedTime(started));
    }

    private async Task<string> DownloadAsync(
        HttpOnnxModelSourceOptions options,
        OnnxModelManifest manifest,
        OnnxModelFile file,
        string directory,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = ComposeUri(options, manifest, file.RelativePath);
        var destination = OnnxModelLayout.FilePath(directory, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var part = OnnxModelLayout.PartPath(destination);

        var present = await AdoptAsync(manifest, file, destination, part, progress, cancellationToken)
            .ConfigureAwait(false);
        if (present is not null)
        {
            return present;
        }

        string? etag = null;
        var attempt = 0;
        var hashFailures = 0;
        Exception? lastFailure = null;

        s_started(_logger, manifest.ModelId, file.RelativePath, uri, null);

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await AttemptAsync(
                    options, manifest, file, uri, part, destination, progress, etag, e => etag = e, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EdgeModelProvisioningException ex) when (ex.Code == EdgeErrorCode.ModelHashMismatch)
            {
                // One retry, and one only: the same wrong bytes twice is a wrong manifest, not a
                // wire error, and a third fetch of 23 MB proves nothing. When the attempts have run
                // out as well it is still rethrown as itself: the two digests are the diagnosis, and
                // "could not be downloaded" would bury them in an InnerException.
                hashFailures++;
                etag = null;
                if (hashFailures >= 2 || attempt >= options.MaxAttempts)
                {
                    throw;
                }

                lastFailure = ex;
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex;
            }
            catch (IOException ex)
            {
                lastFailure = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The body timeout fired, not the caller's cancellation.
                lastFailure = ex;
            }

            if (attempt >= options.MaxAttempts)
            {
                s_downloadFailed(_logger, manifest.ModelId, file.RelativePath, attempt, lastFailure);
                throw new EdgeModelProvisioningException(
                    EdgeErrorCode.ModelDownloadFailed,
                    manifest.ModelId,
                    file.RelativePath,
                    $"'{file.RelativePath}' could not be downloaded from {uri} after {attempt} attempts: " +
                    $"{lastFailure?.Message}",
                    lastFailure)
                {
                    ExpectedBytes = file.SizeBytes,
                    ExpectedSha256 = file.Sha256,
                    SourceUri = uri,
                };
            }

            await DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> AttemptAsync(
        HttpOnnxModelSourceOptions options,
        OnnxModelManifest manifest,
        OnnxModelFile file,
        Uri uri,
        string part,
        string destination,
        IProgress<ModelProvisioningProgress>? progress,
        string? etag,
        Action<string?> recordETag,
        CancellationToken cancellationToken)
    {
        var partInfo = new FileInfo(part);
        var offset = partInfo.Exists ? partInfo.Length : 0;

        if (offset > file.SizeBytes)
        {
            // Longer than the file it is supposed to be a prefix of. No range request repairs that.
            File.Delete(part);
            offset = 0;
        }
        else if (partInfo.Exists && offset == file.SizeBytes)
        {
            // Every byte arrived and the call was cancelled, or the process died, before the digest
            // was checked. Asking the server for "bytes=<size>-" is a 416, which is not retryable,
            // so without this branch the model could never be provisioned on this device again.
            return await CompleteAsync(manifest, file, uri, part, destination, cancellationToken)
                .ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(options.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
        }

        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (etag is not null)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(etag));
            }
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The server holds fewer bytes than the partial file claims to continue from. The
                // partial file is the thing that is wrong, so it goes and the ladder starts over.
                File.Delete(part);
                throw new HttpRequestException(
                    $"{uri} answered 416 to a resume from byte {offset}; the partial file was discarded.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            throw Failed(manifest, file, uri, response.StatusCode);
        }

        var resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (offset > 0 && !resumed)
        {
            // A server that ignores Range returns the whole body with a 200. Appending that to a
            // partial file silently corrupts it, and the SHA-256 check is the only thing standing
            // between that and a session created over garbage.
            s_restarted(_logger, manifest.ModelId, file.RelativePath, offset, null);
            offset = 0;
        }

        recordETag(response.Headers.ETag?.Tag);

        var remaining = response.Content.Headers.ContentLength ?? Math.Max(0, file.SizeBytes - offset);
        var free = FreeSpaceProvider(Path.GetDirectoryName(destination)!);
        if (free is { } available && available < remaining + options.FreeDiskMarginBytes)
        {
            throw new EdgeModelProvisioningException(
                EdgeErrorCode.ModelInsufficientDiskSpace,
                manifest.ModelId,
                file.RelativePath,
                $"'{file.RelativePath}' needs {remaining} more bytes plus a {options.FreeDiskMarginBytes}-byte " +
                $"margin, and the volume holding '{destination}' reports {available} free. Nothing was " +
                "downloaded: filling the device is worse than failing.")
            {
                ExpectedBytes = file.SizeBytes,
                ExpectedSha256 = file.Sha256,
                SourceUri = uri,
            };
        }

        if (resumed)
        {
            s_resumed(_logger, manifest.ModelId, file.RelativePath, offset, null);
        }

        var total = offset + remaining;
        await CopyBodyAsync(
            response, part, offset, resumed, total, options.BodyTimeout, manifest, file, uri, progress, cancellationToken)
            .ConfigureAwait(false);

        return await CompleteAsync(manifest, file, uri, part, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A file an earlier, interrupted call already fetched, verified and renamed is not fetched
    /// again. In a multi-file bundle the weights are rarely the last file, so without this a failure
    /// on a 17 MB tokenizer re-downloads 1.2 GB that is sitting in place and correct.
    /// </summary>
    /// <returns>The file's digest when it is in place and right; null when it has to be fetched.</returns>
    private static async Task<string?> AdoptAsync(
        OnnxModelManifest manifest,
        OnnxModelFile file,
        string destination,
        string part,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        var placed = new FileInfo(destination);
        if (!placed.Exists)
        {
            return null;
        }

        if (placed.Length == file.SizeBytes)
        {
            var actual = await OnnxModelLayout.HashAsync(destination, cancellationToken).ConfigureAwait(false);
            if (string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                progress?.Report(new ModelProvisioningProgress(
                    manifest.ModelId, file.RelativePath, file.SizeBytes, file.SizeBytes, Resumed: true));
                return actual;
            }
        }

        // The wrong length, or the right length and the wrong bytes: not this manifest's file. It
        // would be overwritten by the rename anyway, and until then it is unverified content at a
        // path the fast path trusts by length alone.
        File.Delete(destination);
        return null;
    }

    private async Task<string> CompleteAsync(
        OnnxModelManifest manifest,
        OnnxModelFile file,
        Uri uri,
        string part,
        string destination,
        CancellationToken cancellationToken)
    {
        string actual;
        try
        {
            actual = await OnnxModelLayout
                .VerifyAsync(part, file, manifest.ModelId, uri, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EdgeModelProvisioningException ex) when (ex.Code == EdgeErrorCode.ModelHashMismatch)
        {
            s_hashMismatch(_logger, manifest.ModelId, file.RelativePath, ex.ActualSha256 ?? "unknown", file.Sha256, null);
            throw;
        }

        // Same volume, so this is a rename rather than a copy.
        File.Move(part, destination, overwrite: true);
        ReassertNoBackup(destination);
        return actual;
    }

    private static async Task CopyBodyAsync(
        HttpResponseMessage response,
        string part,
        long offset,
        bool resumed,
        long total,
        TimeSpan bodyTimeout,
        OnnxModelManifest manifest,
        OnnxModelFile file,
        Uri uri,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Its OWN linked token: HttpClient.Timeout stops applying the moment the headers are read,
        // so a body that stalls forever is otherwise never cancelled. It is re-armed after every
        // read that delivered bytes, which is what makes it a stall timeout and not a deadline: an
        // absolute ten minutes is 16 Mbit/s for a 1.2 GB file, and a slower link that never stops
        // moving would fail every attempt it was given.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(bodyTimeout);
        var bodyToken = stall.Token;

        var completed = offset;
        var overran = false;

        var body = await response.Content.ReadAsStreamAsync(bodyToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var target = new FileStream(
                part,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long lastReport = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, bodyToken).ConfigureAwait(false)) > 0)
                {
                    if (completed + read > file.SizeBytes)
                    {
                        // The digest check would catch this too - after the volume was full.
                        overran = true;
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), bodyToken).ConfigureAwait(false);
                    completed += read;
                    stall.CancelAfter(bodyTimeout);

                    // One report per network read is ~15,000 posts to a UI thread for a 1.2 GB file.
                    if (progress is not null &&
                        (completed >= total || lastReport == 0 || Stopwatch.GetElapsedTime(lastReport) >= s_progressInterval))
                    {
                        lastReport = Stopwatch.GetTimestamp();
                        progress.Report(new ModelProvisioningProgress(
                            manifest.ModelId, file.RelativePath, completed, total, resumed));
                    }
                }
            }
        }

        if (overran)
        {
            File.Delete(part);
            throw new EdgeModelProvisioningException(
                EdgeErrorCode.ModelHashMismatch,
                manifest.ModelId,
                file.RelativePath,
                $"{uri} sent more than the {file.SizeBytes} bytes the manifest declares for '{file.RelativePath}'. " +
                "The transfer was stopped and the partial file deleted: a body that long cannot hash to the " +
                "pinned digest, and writing it out to learn that is how a wrong mirror fills a device.")
            {
                ExpectedSha256 = file.Sha256,
                ExpectedBytes = file.SizeBytes,
                SourceUri = uri,
            };
        }
    }

    private static void RefuseATokenInClear(HttpOnnxModelSourceOptions options, OnnxModelManifest manifest)
    {
        if (string.IsNullOrEmpty(options.BearerToken))
        {
            return;
        }

        var uri = ComposeUri(options, manifest, manifest.GraphFile);
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || uri.IsLoopback)
        {
            return;
        }

        throw new EdgeModelProvisioningException(
            EdgeErrorCode.ModelDownloadFailed,
            manifest.ModelId,
            manifest.GraphFile,
            $"HttpOnnxModelSourceOptions.BearerToken is set and {uri.GetLeftPart(UriPartial.Authority)} is not " +
            "HTTPS, so nothing was requested: a bearer token sent in clear is readable by everything on the " +
            "path. Use an https BaseAddress, or clear BearerToken for a mirror that does not need one.")
        {
            SourceUri = uri,
        };
    }

    private static EdgeModelProvisioningException Failed(
        OnnxModelManifest manifest,
        OnnxModelFile file,
        Uri uri,
        HttpStatusCode status)
    {
        var code = (int)status;
        var retryable = code >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

        if (retryable)
        {
            // Surfaced as an HttpRequestException so the retry ladder sees one shape of failure for
            // a 503 and for a severed socket, which are the same thing from the caller's side.
            throw new HttpRequestException(
                $"{uri} answered {code} {status}.",
                inner: null,
                statusCode: status);
        }

        return new EdgeModelProvisioningException(
            EdgeErrorCode.ModelDownloadFailed,
            manifest.ModelId,
            file.RelativePath,
            $"{uri} answered {code} {status}, which is not retryable. Check the repo, the pinned " +
            "revision and - for a gated repo - HttpOnnxModelSourceOptions.BearerToken.")
        {
            ExpectedBytes = file.SizeBytes,
            ExpectedSha256 = file.Sha256,
            SourceUri = uri,
        };
    }

    private static TimeSpan Backoff(int attempt)
    {
        var baseDelay = 200d * Math.Pow(2, attempt - 1);

        // RandomNumberGenerator rather than Random: it is the jitter source no analyzer argues with,
        // and at one call per failed attempt the cost is irrelevant.
        var jitter = RandomNumberGenerator.GetInt32(0, 101);
        return TimeSpan.FromMilliseconds(baseDelay + jitter);
    }

    private static void ReassertNoBackup(string path)
    {
#if IOS || MACCATALYST
        // AFTER the rename, never on the .part file: NSURLIsExcludedFromBackupKey is a resource
        // value on an existing file-system object, and File.Move creates a new one.
        using var url = NSUrl.FromFilename(path);
        url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
#else
        // Android puts the model root under NoBackupFilesDir, which excludes everything beneath it,
        // and desktop has no cloud-backup flag to assert. Named rather than absent so the spec 10.2
        // step has a visible home on every platform.
        _ = path;
#endif
    }
}
