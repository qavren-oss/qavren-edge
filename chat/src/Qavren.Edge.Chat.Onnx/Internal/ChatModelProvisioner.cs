using Microsoft.Extensions.Logging;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Spec section 8.3's provisioner: sub-project 2's model store, reused unchanged, with two things
/// added <i>around</i> it rather than inside it.
/// </summary>
/// <remarks>
/// First, the free-disk pre-check is against the <b>bundle total</b> and happens before the first
/// byte. Sub-project 2's HTTP source checks per file against its own 32 MiB margin, which is the
/// right question for a 23 MiB embedding graph and the wrong one for a 1.241 GB folder: it passes
/// six times for a bundle that will not fit and then fails 900 MB in. Second, the consumer's
/// transfer policy is consulted once, immediately before a connection would be opened.
/// </remarks>
internal sealed class ChatModelProvisioner : IChatModelProvisioner
{
    private static readonly Action<ILogger, string, string, Exception?> s_provisioned =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.ChatModelProvisioned, nameof(EdgeChatEventIds.ChatModelProvisioned)),
            "Chat model {ModelId} is on disk and verified at {Directory}.");

    private readonly ChatPreset _preset;
    private readonly EdgeChatOptions _options;
    private readonly IOnnxModelStore _store;
    private readonly IEdgeModelPaths _modelPaths;
    private readonly ILogger _logger;
    private readonly Func<string, long?> _freeDiskProbe;

    /// <summary>Creates the provisioner for one registration.</summary>
    /// <param name="preset">The preset being provisioned.</param>
    /// <param name="options">That registration's options.</param>
    /// <param name="store">Sub-project 2's model store.</param>
    /// <param name="modelPaths">Sub-project 2's model root.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="freeDiskProbe">
    /// Reads free space on the volume a directory lives on. Null takes the real
    /// <see cref="DriveInfo"/> reading; the tests inject a scripted one, because "the build agent
    /// happens to be short of disk" is not an assertion anybody can write.
    /// </param>
    public ChatModelProvisioner(
        ChatPreset preset,
        EdgeChatOptions options,
        IOnnxModelStore store,
        IEdgeModelPaths modelPaths,
        ILogger logger,
        Func<string, long?>? freeDiskProbe = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(modelPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _preset = preset;
        _options = options;
        _store = store;
        _modelPaths = modelPaths;
        _logger = logger;
        _freeDiskProbe = freeDiskProbe ?? ReadFreeDisk;
    }

    /// <summary>The 16-character content-addressed directory segment: the graph's digest, truncated.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The segment.</returns>
    /// <remarks>
    /// Re-derived here rather than taken from sub-project 2's <c>OnnxModelLayout</c>, which is
    /// <c>internal</c> to <c>Qavren.Edge.Onnx</c> and whose <c>InternalsVisibleTo</c> does not name
    /// this assembly. The rule it implements is public, stated on <c>ProvisionedModel.Directory</c>,
    /// and asserted against a real <c>EnsureAsync</c> in the tier-2 lane.
    /// </remarks>
    public static string Sha16(OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var file in manifest.Files)
        {
            if (string.Equals(file.RelativePath, manifest.GraphFile, StringComparison.Ordinal))
            {
                return file.Sha256.Length <= 16 ? file.Sha256 : file.Sha256[..16];
            }
        }

        throw new ArgumentException(
            $"Manifest '{manifest.ModelId}' names GraphFile '{manifest.GraphFile}', which is not one " +
            $"of its Files; there is no digest to name the model directory after.",
            nameof(manifest));
    }

    /// <summary><c>&lt;Models&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c>.</summary>
    /// <param name="models">The model root.</param>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The absolute model directory.</returns>
    public static string DirectoryFor(string models, OnnxModelManifest manifest) =>
        Path.Combine(models, manifest.ModelId, Sha16(manifest));

    /// <summary>The absolute path of one manifest-relative file.</summary>
    /// <param name="directory">The model directory.</param>
    /// <param name="relativePath">The manifest-relative path, forward-slashed.</param>
    /// <returns>The absolute path.</returns>
    public static string FilePath(string directory, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Where this registration's bytes live.</summary>
    public string Directory =>
        _options.ModelDirectoryOverride ?? DirectoryFor(_modelPaths.Models, _preset.Manifest);

    /// <inheritdoc />
    public bool IsProvisioned =>
        _options.ModelDirectoryOverride is { } overrideDirectory
            ? System.IO.Directory.Exists(overrideDirectory)
            : _store.IsProvisioned(_preset.Manifest);

    /// <inheritdoc />
    public ChatModelPlan Plan()
    {
        var manifest = _preset.Manifest;
        var directory = Directory;
        var provisioned = IsProvisioned;

        long present = 0;
        foreach (var file in manifest.Files)
        {
            // A stat, and nothing more. Never a hash and never a request: this is the method a
            // consent sheet calls on the UI thread.
            var info = new FileInfo(FilePath(directory, file.RelativePath));
            if (info.Exists)
            {
                present += Math.Min(info.Length, file.SizeBytes);
            }
        }

        var total = manifest.TotalSizeBytes;
        var toTransfer = Math.Max(0, total - present);
        var freeDisk = _freeDiskProbe(directory);

        return new ChatModelPlan(
            _preset.Id,
            _preset.DisplayName,
            provisioned,
            total,
            present,
            toTransfer,
            freeDisk,
            FitsOnDisk(freeDisk, toTransfer),
            directory,
            manifest.SpdxLicense,
            _preset.LicenseUri,
            manifest.HuggingFaceRepo,
            manifest.HuggingFaceRevision);
    }

    /// <inheritdoc />
    public async ValueTask<ProvisionedModel> ProvisionAsync(
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsProvisioned)
        {
            var plan = Plan();

            // Before the first byte, not 900 MB in.
            if (!plan.FitsOnDisk)
            {
                throw new EdgeChatException(
                    EdgeErrorCode.ChatInsufficientDiskSpace,
                    $"Provisioning '{_preset.Id}' needs {plan.BytesToTransfer} bytes plus a " +
                    $"{_options.Provisioning.FreeDiskMarginBytes}-byte margin, and " +
                    $"'{plan.Directory}' has {plan.FreeDiskBytes} bytes free.")
                {
                    PresetId = _preset.Id,
                    ModelId = _preset.Manifest.ModelId,
                    RequiredBytes = plan.BytesToTransfer + _options.Provisioning.FreeDiskMarginBytes,
                    AvailableBytes = plan.FreeDiskBytes,
                    Remediation =
                        "Free disk space, pick a smaller preset, or lower " +
                        "ChatProvisioningOptions.FreeDiskMarginBytes once you have measured what " +
                        "this device really needs beyond the bundle.",
                };
            }

            // Consulted once, immediately before a connection would be opened.
            if (_options.Provisioning.IsTransferPermitted is { } permitted && !permitted())
            {
                throw new EdgeChatException(
                    EdgeErrorCode.ChatDownloadNotPermitted,
                    "ChatProvisioningOptions.IsTransferPermitted refused the " +
                    $"{plan.BytesToTransfer}-byte transfer for '{_preset.Id}', so no connection " +
                    "was opened.")
                {
                    PresetId = _preset.Id,
                    ModelId = _preset.Manifest.ModelId,
                    RequiredBytes = plan.BytesToTransfer,
                    Remediation =
                        "This is the app's own policy hook - the usual one is 'wait for an " +
                        "unmetered connection'. Retry when it returns true.",
                };
            }
        }

        var provisioned = await _store
            .EnsureAsync(_preset.Manifest, progress, cancellationToken)
            .ConfigureAwait(false);

        s_provisioned(_logger, provisioned.ModelId, provisioned.Directory, null);
        return provisioned;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(CancellationToken cancellationToken = default) =>
        _store.RemoveAsync(_preset.Manifest.ModelId, cancellationToken);

    private bool FitsOnDisk(long? freeDisk, long bytesToTransfer)
    {
        // Unknown is never a refusal - the same rule the memory gate applies to an unreadable
        // AvailableMemoryBytes, for the same reason.
        if (freeDisk is not { } free)
        {
            return true;
        }

        return free >= bytesToTransfer + _options.Provisioning.FreeDiskMarginBytes;
    }

    private static long? ReadFreeDisk(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
#pragma warning disable CA1031 // A free-space reading nobody can take is unknown, never a failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
