using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Tier3;

/// <summary>
/// Spec 14.5's requirement is a LANE - files produced by actual Word, LibreOffice and Acrobat -
/// and one Word file three times is not one. Skips with the printed reason while the manifest is
/// empty, so the owed item is visible in the output of every local and CI run rather than only in
/// the plan (plan adjustment 26).
/// </summary>
public sealed class RealWorldCorpusCoverageTests
{
    [Fact]
    public void Each_of_the_three_producers_is_represented_at_least_once()
    {
        var entries = RealWorldCorpus.Load();
        Assert.SkipWhen(entries.Count == 0, RealWorldCorpus.OwedReason);

        var producers = entries.Select(e => e.Producer).ToHashSet(StringComparer.Ordinal);
        foreach (var producer in RealWorldCorpus.Producers)
        {
            Assert.True(
                producers.Contains(producer),
                $"spec 14.5 names '{producer}' and the manifest carries no document produced by it: {string.Join(", ", producers)}");
        }
    }
}
