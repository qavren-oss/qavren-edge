namespace Qavren.Edge.Rag.Tests.Fakes;

/// <summary>The two-source corpus most of the middleware tests run over.</summary>
public static class TestCorpus
{
    /// <summary>The first chunk's body.</summary>
    public const string FirstText = "Warranty runs 24 months from delivery.";

    /// <summary>The second chunk's body.</summary>
    public const string SecondText = "Returns are accepted within 30 days.";

    /// <summary>Two distance-scored sources, best first, with no <c>Ordinal</c> yet.</summary>
    /// <returns>The sources.</returns>
    public static IReadOnlyList<RagSource> Two() =>
    [
        new RagSource("chunk-a", FirstText)
        {
            Title = "Warranty terms",
            Uri = new Uri("https://example.com/warranty"),
            Score = 0.10,
            ScoreKind = RetrievalScoreKind.Distance,
        },
        new RagSource("chunk-b", SecondText)
        {
            Title = "Returns",
            Uri = new Uri("https://example.com/returns"),
            Score = 0.20,
            ScoreKind = RetrievalScoreKind.Distance,
        },
    ];

    /// <summary>The rows a fake MEVD collection is built from.</summary>
    /// <returns>Three rows, whose two lanes rank them in opposite orders.</returns>
    public static IReadOnlyList<FakeRow> Rows() =>
    [
        new FakeRow(new TestRecord { Key = "chunk-a", Text = FirstText, Title = "Warranty terms" }, 0.10, 0.01),
        new FakeRow(new TestRecord { Key = "chunk-b", Text = SecondText, Title = "Returns" }, 0.20, 0.02),
        new FakeRow(new TestRecord { Key = "chunk-c", Text = "Shipping takes two days.", Title = "Shipping" }, 0.30, 0.03),
    ];
}
