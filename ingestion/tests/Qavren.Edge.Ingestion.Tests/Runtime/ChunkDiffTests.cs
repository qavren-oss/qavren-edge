using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>Spec 9.3's identity and spec 9.4 step 5's set difference, over hand-built sets.</summary>
public sealed class ChunkDiffTests
{
    [Fact]
    public void Identify_assigns_duplicate_ordinals_within_one_document()
    {
        var drafts = new[]
        {
            Draft(0, "alpha"),
            Draft(1, "alpha"),
            Draft(2, "alpha"),
            Draft(3, "beta"),
        };

        var identified = ChunkDiff.Identify("src", "doc.md", drafts);

        Assert.Equal([0, 1, 2, 0], identified.Select(c => c.DuplicateOrdinal));
        Assert.Equal(4, identified.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(identified, c => Assert.Equal(32, c.Key.Length));
    }

    [Fact]
    public void Identity_excludes_the_ordinal_so_a_moved_chunk_keeps_its_key()
    {
        var first = ChunkDiff.Identify("src", "doc.md", [Draft(0, "alpha", charStart: 0)]);
        var moved = ChunkDiff.Identify("src", "doc.md", [Draft(7, "alpha", charStart: 400)]);

        Assert.Equal(first[0].Key, moved[0].Key);
    }

    [Fact]
    public void Identity_is_scoped_to_the_source_and_the_document()
    {
        var a = ChunkDiff.Identify("src", "a.md", [Draft(0, "same")]);
        var b = ChunkDiff.Identify("src", "b.md", [Draft(0, "same")]);
        var c = ChunkDiff.Identify("other", "a.md", [Draft(0, "same")]);

        Assert.NotEqual(a[0].Key, b[0].Key);
        Assert.NotEqual(a[0].Key, c[0].Key);
    }

    [Fact]
    public void Compare_splits_added_removed_and_unchanged()
    {
        var fresh = ChunkDiff.Identify("src", "doc.md", [Draft(0, "kept"), Draft(1, "new")]);
        var stored = new[]
        {
            Row(ChunkDiff.Identify("src", "doc.md", [Draft(0, "kept")])[0], ordinal: 0, end: 4),
            Row(ChunkDiff.Identify("src", "doc.md", [Draft(0, "gone")])[0], ordinal: 1, end: 4),
        };

        var diff = ChunkDiff.Compare(fresh, stored);

        Assert.Single(diff.Added);
        Assert.Equal("new", diff.Added[0].Draft.Text);
        Assert.Single(diff.Removed);
        Assert.Single(diff.Unchanged);
        Assert.Empty(diff.Repaired);
    }

    [Fact]
    public void An_insertion_at_the_head_repairs_the_tail_and_embeds_only_the_new_chunk()
    {
        // Three paragraphs stored at ordinals 0, 1, 2.
        var original = ChunkDiff.Identify(
            "src", "doc.md", [Draft(0, "one", 0, 3), Draft(1, "two", 3, 6), Draft(2, "three", 6, 11)]);
        var stored = original
            .Select((c, i) => Row(c, i, c.Draft.CharStart, c.Draft.CharEnd))
            .ToArray();

        // A paragraph is inserted at the top: every later chunk shifts by one ordinal and four
        // characters, but its CONTENT - and therefore its key - is unchanged.
        var fresh = ChunkDiff.Identify(
            "src",
            "doc.md",
            [Draft(0, "zero", 0, 4), Draft(1, "one", 4, 7), Draft(2, "two", 7, 10), Draft(3, "three", 10, 15)]);

        var diff = ChunkDiff.Compare(fresh, stored);

        Assert.Single(diff.Added);
        Assert.Equal("zero", diff.Added[0].Draft.Text);
        Assert.Empty(diff.Removed);
        Assert.Equal(3, diff.Unchanged.Count);
        Assert.Equal(3, diff.Repaired.Count);
    }

    [Fact]
    public void A_document_that_repeats_a_paragraph_three_times_round_trips()
    {
        var fresh = ChunkDiff.Identify(
            "src", "doc.md", [Draft(0, "same", 0, 4), Draft(1, "same", 4, 8), Draft(2, "same", 8, 12)]);
        var stored = fresh.Select(c => Row(c, c.Draft.Ordinal, c.Draft.CharStart, c.Draft.CharEnd)).ToArray();

        var diff = ChunkDiff.Compare(fresh, stored);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Equal(3, diff.Unchanged.Count);
        Assert.Empty(diff.Repaired);
    }

    private static ChunkDraft Draft(int ordinal, string text, int charStart = 0, int charEnd = 0) =>
        new(ordinal, text, text, [], null, charStart, charEnd == 0 ? charStart + text.Length : charEnd,
            TokenCount: 1, DocumentBlockKind.Paragraph, Page: -1);

    private static StoredChunkRow Row(IdentifiedChunk chunk, int ordinal, int start = 0, int end = 0) =>
        new(chunk.Key, chunk.ContentHash, ordinal, start, end);
}
