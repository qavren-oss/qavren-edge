namespace Qavren.Edge.Rag.Internal;

/// <summary>
/// The counters spec 14.3's <c>"Qavren.Edge.Rag"</c> diagnostics block reports. One instance per
/// client; the contributor reaches them through <c>GetService</c>, which is how a report describes
/// the pipeline the app actually built rather than the one it registered.
/// </summary>
internal sealed class RagStatistics
{
    private long _asks;
    private long _retrievalFailures;
    private long _extractiveAnswers;

    internal long Asks => Interlocked.Read(ref _asks);

    internal long RetrievalFailures => Interlocked.Read(ref _retrievalFailures);

    internal long ExtractiveAnswers => Interlocked.Read(ref _extractiveAnswers);

    internal int LastRetrievedCount { get; set; }

    internal double LastRetrievalMs { get; set; }

    internal RetrievalScoreKind? LastScoreKind { get; set; }

    internal int LastContextTokens { get; set; }

    internal int LastCitationsAttached { get; set; }

    internal int LastCitationsUnresolved { get; set; }

    internal bool LastGrounded { get; set; }

    internal void Ask() => Interlocked.Increment(ref _asks);

    internal long RetrievalFailed() => Interlocked.Increment(ref _retrievalFailures);

    internal void ExtractiveAnswered() => Interlocked.Increment(ref _extractiveAnswers);
}
