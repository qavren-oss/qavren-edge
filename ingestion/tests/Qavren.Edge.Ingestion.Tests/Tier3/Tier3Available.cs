namespace Qavren.Edge.Ingestion.Tests.Tier3;

/// <summary>
/// The tier-3 <c>SkipUnless</c> gates, evaluated at RUNTIME - after the class constructor - so a
/// skipped tier-3 test never loads a 23 MB model or opens a fetched document. Set only by
/// <c>ci.yml</c>'s nightly <c>model-tests</c> job and by a developer opting in.
/// </summary>
/// <remarks>
/// These live on their own type so every fact names it with <c>SkipType = typeof(Tier3Available)</c>:
/// xunit resolves <c>SkipUnless</c> against the test class unless <c>SkipType</c> says otherwise.
/// </remarks>
public static class Tier3Available
{
    /// <summary>The tier-3 lane itself: <c>QAVREN_EDGE_TIER3</c> is set and is not <c>0</c>.</summary>
    public static bool Yes =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_TIER3") is { Length: > 0 } flag
        && !string.Equals(flag, "0", StringComparison.Ordinal);

    /// <summary>The lane AND the pinned int8 MiniLM staged in <c>QAVREN_EDGE_MODEL_DIR</c>.</summary>
    public static bool WithModel =>
        Yes
        && StagedModelDirectory is { } dir
        && File.Exists(Path.Combine(dir, "onnx", "model_qint8_arm64.onnx"))
        && File.Exists(Path.Combine(dir, "vocab.txt"));

    /// <summary>The lane AND a directory the fetch step wrote real-world documents into.</summary>
    public static bool WithDocuments => Yes && DocumentsDirectory is { } dir && Directory.Exists(dir);

    /// <summary><c>QAVREN_EDGE_MODEL_DIR</c>, or null.</summary>
    public static string? StagedModelDirectory =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_MODEL_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary><c>QAVREN_EDGE_DOCS_DIR</c>, or null.</summary>
    public static string? DocumentsDirectory =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_DOCS_DIR") is { Length: > 0 } dir ? dir : null;
}
