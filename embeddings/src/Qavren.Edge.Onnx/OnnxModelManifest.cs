namespace Qavren.Edge.Onnx;

/// <summary>What a file in a model manifest is for.</summary>
public enum OnnxModelFileRole
{
    /// <summary>The <c>.onnx</c> graph itself.</summary>
    Graph,

    /// <summary>
    /// External weight data that must land beside the graph under its exact filename - ORT reads
    /// the name out of the graph's <c>external_data</c> records, so renaming it breaks the load.
    /// </summary>
    GraphExternalData,

    /// <summary>A WordPiece <c>vocab.txt</c> or equivalent.</summary>
    Vocabulary,

    /// <summary>A <c>tokenizer.json</c> or a SentencePiece model.</summary>
    TokenizerModel,

    /// <summary>Anything else the consumer wants provisioned alongside.</summary>
    Auxiliary,
}

/// <summary>One file a model needs on disk.</summary>
/// <param name="RelativePath">
/// The path under the model directory, always with forward slashes: it is the same string the
/// Hugging Face URL carries.
/// </param>
/// <param name="Role">What the file is for.</param>
/// <param name="SizeBytes">The exact size, used by the fast path and by the free-disk check.</param>
/// <param name="Sha256">
/// Lowercase hex. On Hugging Face that is the git-LFS <c>oid</c> - never the <c>xetHash</c> those
/// Xet-backed repos also return.
/// </param>
public sealed record OnnxModelFile(string RelativePath, OnnxModelFileRole Role, long SizeBytes, string Sha256);

/// <summary>Everything needed to put one model on disk and prove it is the right one.</summary>
public sealed record OnnxModelManifest
{
    /// <summary>The id a consumer passes to <c>IOnnxSessionHost.AcquireAsync</c>.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// A LIST, not one entry: some ONNX mirrors split weights into external data (a 56 KB
    /// <c>model.onnx</c> beside a 90 MB <c>model.onnx_data</c>) and the sidecar must land next to
    /// the graph under its exact filename. No shipped preset does, but the type must allow it.
    /// </summary>
    public required IReadOnlyList<OnnxModelFile> Files { get; init; }

    /// <summary>The <see cref="OnnxModelFile.RelativePath"/> of the entry ORT is handed.</summary>
    public required string GraphFile { get; init; }

    /// <summary>The SPDX identifier of the model's licence, for an about screen.</summary>
    public required string SpdxLicense { get; init; }

    /// <summary>The <c>owner/name</c> repo, when the model comes from the Hub.</summary>
    public string? HuggingFaceRepo { get; init; }

    /// <summary>
    /// A full commit SHA. Never <c>"main"</c>: a moving revision silently changes the vectors, and
    /// vectors written by two revisions of one model are not comparable.
    /// </summary>
    public string? HuggingFaceRevision { get; init; }

    /// <summary>The sum of every file's size. What the memory pre-flight budgets against.</summary>
    public long TotalSizeBytes
    {
        get
        {
            long total = 0;
            for (var i = 0; i < Files.Count; i++)
            {
                total += Files[i].SizeBytes;
            }

            return total;
        }
    }
}

/// <summary>Where a provisioned model's bytes came from.</summary>
public enum ModelProvisioningSource
{
    /// <summary>The marker was present and every file's length matched. No hashing, no I/O.</summary>
    AlreadyPresent,

    /// <summary>Copied in from a directory already on the device.</summary>
    File,

    /// <summary>Copied out of an app package asset.</summary>
    Bundled,

    /// <summary>Fetched over HTTP.</summary>
    Downloaded,
}

/// <summary>A model that is on disk, verified and ready to hand to ORT.</summary>
/// <param name="ModelId">The manifest's model id.</param>
/// <param name="Directory"><c>&lt;Models&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c>.</param>
/// <param name="GraphPath">The absolute path ORT is handed.</param>
/// <param name="GraphSha256">The graph's lowercase-hex digest; the CoreML cache key is built from it.</param>
/// <param name="Files">Manifest-relative path to absolute path, for every file in the manifest.</param>
/// <param name="Source">Where the bytes came from.</param>
/// <param name="Duration">How long provisioning took.</param>
public sealed record ProvisionedModel(
    string ModelId,
    string Directory,
    string GraphPath,
    string GraphSha256,
    IReadOnlyDictionary<string, string> Files,
    ModelProvisioningSource Source,
    TimeSpan Duration);

/// <summary>One progress report, for a determinate bar.</summary>
/// <param name="ModelId">The model being provisioned.</param>
/// <param name="RelativePath">The file being provisioned.</param>
/// <param name="BytesCompleted">Bytes on disk so far, including any resumed prefix.</param>
/// <param name="BytesTotal">The expected total, or null when the server declared none.</param>
/// <param name="Resumed">
/// True when this transfer continued a partial file. False after a server ignored the range
/// request and the <c>.part</c> file was truncated.
/// </param>
public readonly record struct ModelProvisioningProgress(
    string ModelId,
    string RelativePath,
    long BytesCompleted,
    long? BytesTotal,
    bool Resumed);
