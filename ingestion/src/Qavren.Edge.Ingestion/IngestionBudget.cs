namespace Qavren.Edge.Ingestion;

/// <summary>
/// A ceiling a run suspends against (spec 10.2). Every member is null by default, so a budget
/// that sets nothing is <see cref="Unlimited"/>.
/// </summary>
public sealed record IngestionBudget
{
    /// <summary>Wall time. Checked before each document and before each write window.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Documents processed, skips included.</summary>
    public int? MaxDocuments { get; init; }

    /// <summary>Chunks added.</summary>
    public int? MaxChunks { get; init; }

    /// <summary>
    /// Tokens embedded, metered against exactly the number <c>IngestionRunResult.TokensEmbedded</c>
    /// reports, so a budget and a report can never disagree about what a token is.
    /// </summary>
    public long? MaxTokens { get; init; }

    /// <summary>No ceiling of any kind. The default when <c>IngestionRunOptions.Budget</c> is null.</summary>
    public static IngestionBudget Unlimited { get; } = new();

    /// <summary>
    /// 20 seconds of wall time, and nothing else. Fits inside an iOS <c>BGAppRefreshTask</c> slot.
    /// <see cref="MaxDocuments"/>, <see cref="MaxChunks"/> and <see cref="MaxTokens"/> stay null
    /// deliberately: a Quick that also capped documents would suspend on a count that has nothing
    /// to do with the twenty-second slot it exists to fit. Compose one instead —
    /// <c>IngestionBudget.Quick with { MaxDocuments = 50 }</c>.
    /// </summary>
    public static IngestionBudget Quick { get; } = new() { MaxDuration = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// 5 minutes of wall time, and nothing else. Fits inside a WorkManager Worker's 10-minute cap.
    /// </summary>
    public static IngestionBudget Background { get; } = new() { MaxDuration = TimeSpan.FromMinutes(5) };
}
