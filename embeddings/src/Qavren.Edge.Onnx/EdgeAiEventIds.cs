namespace Qavren.Edge.Onnx;

/// <summary>Continues SP1's EdgeEventIds numbering. 600-899 is sub-project 2's range.</summary>
/// <remarks>
/// Every one of these is consumed through <c>LoggerMessage.Define</c>: this repo's
/// <c>TreatWarningsAsErrors</c> plus <c>latest-recommended</c> makes CA1848 an error, as SP1's
/// <c>EdgeHost</c> already discovered. SP1's <c>EdgeEventIds</c> is a non-partial static class, so
/// sub-project 2 publishes its own rather than extending it.
/// </remarks>
public static class EdgeAiEventIds
{
    /// <summary>The ORT environment was created by this library.</summary>
    public const int OrtEnvironmentCreated = 600;

    /// <summary>Another library created the ORT environment first; ours continues regardless.</summary>
    public const int OrtEnvironmentPreexisting = 601;

    /// <summary>A line forwarded out of ORT's native logger into <c>ILogger</c>.</summary>
    public const int OrtLog = 602;

    /// <summary>An inference session was created.</summary>
    public const int SessionLoaded = 620;

    /// <summary>Session creation failed.</summary>
    public const int SessionLoadFailed = 621;

    /// <summary>A session was dropped, by memory pressure or by shutdown.</summary>
    public const int SessionDropped = 622;

    /// <summary>An execution provider refused to append; the loop fell through to the next one.</summary>
    public const int ExecutionProviderSkipped = 623;

    /// <summary>An execution provider appended without throwing.</summary>
    public const int ExecutionProviderAccepted = 624;

    /// <summary>A model is present, verified and ready.</summary>
    public const int ModelProvisioned = 640;

    /// <summary>A model download started.</summary>
    public const int ModelDownloadStarted = 641;

    /// <summary>A model download resumed from a partial file.</summary>
    public const int ModelDownloadResumed = 642;

    /// <summary>A model download restarted because the server ignored the range request.</summary>
    public const int ModelDownloadRestarted = 643;

    /// <summary>A downloaded file did not match its expected SHA-256.</summary>
    public const int ModelHashMismatch = 644;

    /// <summary>A model download failed after every attempt.</summary>
    public const int ModelDownloadFailed = 645;

    /// <summary>A stale CoreML cache subtree was removed.</summary>
    public const int OrtCachePurged = 646;

    /// <summary>One embedding batch finished.</summary>
    public const int EmbeddingBatchCompleted = 700;

    /// <summary>An input was truncated to the preset's maximum sequence length.</summary>
    public const int EmbeddingInputTruncated = 701;

    /// <summary>The warm-up batch finished.</summary>
    public const int EmbeddingWarmUpCompleted = 702;

    /// <summary>The effective batch size was reduced under memory pressure.</summary>
    public const int EmbeddingBatchShrunk = 703;

    /// <summary>A vector collection's tables were created.</summary>
    public const int CollectionCreated = 800;

    /// <summary>A vector collection's tables were dropped.</summary>
    public const int CollectionDropped = 801;

    /// <summary>A hybrid search ran.</summary>
    public const int HybridSearchExecuted = 802;

    /// <summary>An FTS5 merge finished.</summary>
    public const int FtsMergeCompleted = 803;

    /// <summary>An unsupported index kind was requested and ignored.</summary>
    public const int IndexKindIgnored = 804;
}
