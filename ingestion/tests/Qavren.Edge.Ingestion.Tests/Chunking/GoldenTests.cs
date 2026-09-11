using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// The twenty golden comparisons (spec 14.1, plan adjustment 12). Every one is generated against
/// <c>MlChunkTokenizer</c> over the digest-checked <c>bert-base-uncased</c> vocabulary at the
/// MiniLM triple <b>222 / 32 / 27</b>.
/// </summary>
/// <remarks>
/// Two rules make a golden test a gate rather than a rubber stamp, and both are SP2's
/// <c>reference-vectors.json</c> rule: goldens are written ONLY under
/// <c>QAVREN_EDGE_WRITE_GOLDEN=1</c>, and <b>the writer refuses to overwrite an existing file</b>.
/// Deleting one is the deliberate act, and regeneration is its own PR with the reason in the body.
/// </remarks>
public sealed class GoldenTests
{
    public static TheoryData<string> Goldens
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var golden in GoldenCases.All)
            {
                data.Add(golden.Golden);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public async Task Golden_matches_the_committed_boundaries(string golden)
    {
        ChunkingHarness.RequireVocabulary();

        var actual = GoldenFile.Render(await ChunkAsync(GoldenCases.Find(golden)));
        var committed = GoldenFile.Embedded(golden);

        if (committed is null)
        {
            GoldenFile.TryWrite(Path.Combine(ChunkingHarness.FixturesDirectory(), "golden"), golden, actual);
            Assert.Skip(
                $"Golden '{golden}' is not committed yet. Re-run with QAVREN_EDGE_WRITE_GOLDEN=1 to " +
                "generate it, then rebuild so it is embedded.");
            return;
        }

        Assert.Equal(GoldenFile.Normalise(committed), actual);
    }

    [Fact]
    public void Twenty_goldens_are_declared_with_the_12_5_3_composition()
    {
        Assert.Equal(20, GoldenCases.All.Count);
        Assert.Equal(20, GoldenCases.All.Select(c => c.Golden).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, GoldenCases.All.Count(c => c.Golden.EndsWith(".auto.json", StringComparison.Ordinal)));
        Assert.Equal(
            5,
            GoldenCases.All.Count(c => c.Golden.Contains(".markdown-heading.", StringComparison.Ordinal)));
        Assert.Equal(
            3,
            GoldenCases.All.Count(c => c.Golden.EndsWith(".token-window.json", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_declared_golden_is_committed_and_embedded()
    {
        var missing = GoldenCases.All
            .Where(c => GoldenFile.Embedded(c.Golden) is null)
            .Select(c => c.Golden)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Goldens declared but not embedded: " + string.Join(", ", missing));
    }

    [Fact]
    public void Resolved_budget_is_the_pinned_MiniLM_triple()
    {
        ChunkingHarness.RequireVocabulary();

        var resolved = new ChunkOptions().Resolve(ChunkingHarness.MiniLm, ChunkingHarness.Tokenizer);

        Assert.Equal(222, resolved.MaxTokens);
        Assert.Equal(32, resolved.OverlapTokens);
        Assert.Equal(27, resolved.MinTokens);
        Assert.Equal(32, resolved.HeadingPathTokenBudget);
        Assert.Equal(2, resolved.SpecialTokenOverhead);
        Assert.Equal(0, resolved.DocumentPrefixTokens);
    }

    [Fact]
    public void Writer_refuses_to_overwrite_an_existing_golden()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qedge-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN");
        try
        {
            Environment.SetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN", "1");

            Assert.True(GoldenFile.TryWrite(directory, "sample.auto.json", "[]"));
            Assert.False(GoldenFile.TryWrite(directory, "sample.auto.json", "[{}]"));
            Assert.Equal("[]", File.ReadAllText(Path.Combine(directory, "sample.auto.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Writer_does_nothing_without_the_environment_gate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qedge-golden-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN");
        try
        {
            Environment.SetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN", null);

            Assert.False(GoldenFile.TryWrite(directory, "sample.auto.json", "[]"));
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN", previous);
        }
    }

    private static async Task<List<ChunkDraft>> ChunkAsync(GoldenCase golden)
    {
        var document = await ChunkingHarness.ExtractAsync(golden.Fixture).ConfigureAwait(false);
        var options = ChunkingHarness.Options(golden.Variant);
        var chunker = ChunkerFactory.Resolve(golden.ChunkerId, document.MediaType, new CapturingLogger());

        return [.. chunker.Chunk(document, options, ChunkingHarness.Tokenizer)];
    }
}
