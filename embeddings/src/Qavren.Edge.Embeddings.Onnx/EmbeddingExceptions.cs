namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>
/// Every failure this package raises. It carries exactly the five codes spec 15.1 allocates to
/// <c>Qavren.Edge.Embeddings.Onnx</c> - <see cref="EdgeErrorCode.TokenizerAssetMissing"/> (5101),
/// <see cref="EdgeErrorCode.TokenizerKindUnsupported"/> (5102),
/// <see cref="EdgeErrorCode.EmbeddingDimensionMismatch"/> (5103),
/// <see cref="EdgeErrorCode.EmbeddingPresetNotFound"/> (5104) and
/// <see cref="EdgeErrorCode.EmbeddingInputTooLong"/> (5105) - and invents none: 15.1 is a closed
/// list and a sixth code would be an SP1 edit this plan does not have.
/// </summary>
/// <remarks>
/// Derives from SP1's <see cref="EdgeException"/>, so the <c>HelpLink</c> convention
/// (<c>foundation/docs/errors.md#&lt;code&gt;</c>) applies for free.
/// </remarks>
public sealed class EdgeEmbeddingException : EdgeException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The error code; the help link is derived from it.</param>
    /// <param name="presetId">The preset this failure concerns.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, when there is one.</param>
    public EdgeEmbeddingException(
        EdgeErrorCode code,
        string presetId,
        string message,
        Exception? innerException = null)
        : base(code, message, innerException)
    {
        PresetId = presetId;
    }

    /// <summary>The preset this failure concerns.</summary>
    public string PresetId { get; }

    /// <summary>The width or size that was required. Populated by dimension and vocabulary mismatches only.</summary>
    public int? Expected { get; init; }

    /// <summary>The width or size that was actually seen. Populated by dimension and vocabulary mismatches only.</summary>
    public int? Actual { get; init; }
}
