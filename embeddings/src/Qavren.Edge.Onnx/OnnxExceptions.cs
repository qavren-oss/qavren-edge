namespace Qavren.Edge.Onnx;

/// <summary>
/// Every ONNX-runtime failure that is not a provisioning failure: an unsupported RID, a refused
/// execution provider, a memory pre-flight refusal, a signature mismatch, a session that would not
/// create, and the <see cref="EdgeErrorCode.OnnxStaticShapesUnpinned"/> guard.
/// </summary>
/// <remarks>
/// Derives from SP1's <see cref="EdgeException"/>, so the <c>HelpLink</c> convention
/// (<c>foundation/docs/errors.md#&lt;code&gt;</c>) applies for free.
/// </remarks>
public sealed class EdgeOnnxException : EdgeException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The error code; the help link is derived from it.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, when there is one.</param>
    public EdgeOnnxException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }

    /// <summary>The model this failure concerns, when it concerns one.</summary>
    public string? ModelId { get; init; }

    /// <summary>The host runtime identifier, as ORT sees it.</summary>
    public string? RuntimeIdentifier { get; init; }

    /// <summary>Every execution-provider attempt made before the failure, in order.</summary>
    public IReadOnlyList<ExecutionProviderAttempt>? ExecutionProviders { get; init; }

    /// <summary>The bytes the operation needed, when a memory or disk budget was the cause.</summary>
    public long? RequiredBytes { get; init; }

    /// <summary>The bytes the OS reported available.</summary>
    public long? AvailableBytes { get; init; }

    /// <summary>What the caller can do about it, in one sentence.</summary>
    public string? Remediation { get; init; }
}

/// <summary>
/// A model file could not be produced: missing asset, short disk, failed download, or a digest
/// that did not match the manifest.
/// </summary>
public sealed class EdgeModelProvisioningException : EdgeException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The error code; the help link is derived from it.</param>
    /// <param name="modelId">The model whose provisioning failed.</param>
    /// <param name="relativePath">The manifest-relative file that failed.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, when there is one.</param>
    public EdgeModelProvisioningException(
        EdgeErrorCode code,
        string modelId,
        string relativePath,
        string message,
        Exception? innerException = null)
        : base(code, message, innerException)
    {
        ModelId = modelId;
        RelativePath = relativePath;
    }

    /// <summary>The model whose provisioning failed.</summary>
    public string ModelId { get; }

    /// <summary>The manifest-relative file that failed.</summary>
    public string RelativePath { get; }

    /// <summary>The digest the manifest declared, lowercase hex.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>The digest the bytes on disk actually hash to, lowercase hex.</summary>
    public string? ActualSha256 { get; init; }

    /// <summary>The size the manifest declared.</summary>
    public long? ExpectedBytes { get; init; }

    /// <summary>The size the bytes on disk actually are.</summary>
    public long? ActualBytes { get; init; }

    /// <summary>
    /// Where the bytes were being fetched from, when they were being fetched.
    /// </summary>
    /// <remarks>
    /// Spec 6.5 spells this <c>Source</c>. It cannot be: <see cref="Exception.Source"/> already
    /// exists, is a <see cref="string"/>, and is written by the runtime, so a <see cref="Uri"/>
    /// member of that name is CS0114 and would have to shadow with <c>new</c> - leaving two
    /// differently-typed <c>Source</c> members on one exception for every logger and serializer to
    /// pick between. Renamed rather than shadowed.
    /// </remarks>
    public Uri? SourceUri { get; init; }
}
