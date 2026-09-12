namespace Qavren.Edge.Ingestion;

/// <summary>
/// Every log event Qavren.Edge.Ingestion emits, at spec section 11's numbers.
/// </summary>
/// <remarks>
/// 926 (<c>FtsMergeCompleted</c>) is deliberately ABSENT — plan adjustment 2 deleted SP3's own
/// FTS5 sidecar merge along with the four <c>FtsMerge*</c> members of <c>IngestionOptions</c>.
/// The gap is left rather than closed by renumbering 927, so a log filter written against the
/// published spec keeps matching every id that still exists.
/// </remarks>
public static class EdgeIngestionEventIds
{
    public const int RunStarted = 900;
    public const int RunCompleted = 901;
    public const int RunSuspended = 902;
    public const int RunFailed = 903;
    public const int DocumentSkipped = 904;
    public const int DocumentIndexed = 905;
    public const int DocumentNoTextLayer = 906;
    public const int DocumentFailed = 907;
    public const int DocumentUnsupported = 908;
    public const int ExtractorSelected = 909;
    public const int HeadingPathTruncated = 910;
    public const int ChunkMergedUp = 911;
    public const int StaleDocumentsPruned = 912;
    public const int ExtractionStreamingMode = 913;
    public const int EncodingFallback = 914;
    public const int RecipeChanged = 915;
    public const int OrdinalsRepaired = 916;
    public const int MediImagesDropped = 917;
    public const int EmbedBatchShrunk = 918;
    public const int ThrottleAdjusted = 919;
    public const int ThrottlePaused = 920;
    public const int ChunkTruncated = 921;
    public const int ChunkerFellBack = 922;
    public const int ExtractionWarning = 923;
    public const int PageTimedOut = 924;
    public const int StateCommitted = 925;

    // 926 is a gap. See the remarks above.
    public const int HeadingSanitised = 927;
}
