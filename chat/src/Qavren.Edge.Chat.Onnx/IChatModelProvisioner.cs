using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

/// <summary>What a consent screen needs, and nothing it does not. No network I/O beyond a stat.</summary>
/// <param name="PresetId">The preset being planned.</param>
/// <param name="DisplayName">What the sheet shows.</param>
/// <param name="Provisioned">Whether the bytes are already on disk and verified.</param>
/// <param name="TotalBytes">The bundle total - every file in the manifest, not just the weights.</param>
/// <param name="BytesAlreadyPresent">What a resumed transfer would not have to fetch again.</param>
/// <param name="BytesToTransfer"><see cref="TotalBytes"/> minus <see cref="BytesAlreadyPresent"/>.</param>
/// <param name="FreeDiskBytes">
/// Free space on the volume the model directory lives on, or null when the platform would not say.
/// <b>Null is unknown and never a refusal</b>, exactly as an unreadable available-memory figure is.
/// </param>
/// <param name="FitsOnDisk">
/// Whether <see cref="BytesToTransfer"/> plus <c>ChatProvisioningOptions.FreeDiskMarginBytes</c>
/// fits in <see cref="FreeDiskBytes"/>. True when the free-space reading is unknown.
/// </param>
/// <param name="Directory">Where the bytes will land.</param>
/// <param name="SpdxLicense">
/// The manifest's SPDX <i>expression</i>. <c>LicenseRef-LLAMA-3.2-Community</c> is a legal one; a
/// bare <c>LLAMA-3.2-Community</c> is not, which is why the field carries the prefixed spelling.
/// </param>
/// <param name="LicenseUri">The licence text a consent sheet links to.</param>
/// <param name="HuggingFaceRepo">The <c>owner/name</c> repo, when the model comes from the Hub.</param>
/// <param name="HuggingFaceRevision">A full commit SHA. Never <c>"main"</c>.</param>
public sealed record ChatModelPlan(
    string PresetId,
    string DisplayName,
    bool Provisioned,
    long TotalBytes,
    long BytesAlreadyPresent,
    long BytesToTransfer,
    long? FreeDiskBytes,
    bool FitsOnDisk,
    string Directory,
    string SpdxLicense,
    Uri? LicenseUri,
    string? HuggingFaceRepo,
    string? HuggingFaceRevision);

/// <summary>
/// The only way a chat model's bytes arrive. Nothing downloads implicitly:
/// <c>IChatModelHost.AcquireAsync</c> on an unprovisioned model throws
/// <see cref="EdgeErrorCode.ChatModelNotProvisioned"/> rather than starting a 1.24 GB transfer. The
/// <see cref="Plan"/> - consent - <see cref="ProvisionAsync"/> sequence is what App Store Review
/// 4.2.3(ii) requires ("disclose the size of the download and prompt users before doing so"),
/// expressed in the API rather than left to a README.
/// </summary>
public interface IChatModelProvisioner
{
    /// <summary>Everything a consent sheet needs. No network I/O beyond a stat.</summary>
    /// <returns>The plan.</returns>
    ChatModelPlan Plan();

    /// <summary>Whether the marker is present and every file's length matches.</summary>
    bool IsProvisioned { get; }

    /// <summary>
    /// Pre-checks free disk against the <b>bundle</b> total, then delegates to sub-project 2's
    /// <c>IOnnxModelStore.EnsureAsync</c> - resumable ranged GET, per-file SHA-256 against the
    /// git-LFS oid, atomic marker, stale-revision purge - all reused unchanged.
    /// </summary>
    /// <param name="progress">Optional progress, for a determinate bar.</param>
    /// <param name="cancellationToken">Cancellation. Never converted.</param>
    /// <returns>The provisioned model.</returns>
    ValueTask<ProvisionedModel> ProvisionAsync(
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the model directory.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes once the tree is gone.</returns>
    ValueTask RemoveAsync(CancellationToken cancellationToken = default);
}
