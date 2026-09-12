using Qavren.Edge.Rag;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec 16.1's cross-package literal: the pin key <c>Qavren.Edge.Rag</c> stamps on the injected
/// context message and the key <c>Qavren.Edge.Chat.Onnx</c>'s reducer treats as pinned are
/// deliberately duplicated - the two packages do not reference each other and must not - and this
/// is the guard that keeps the duplicates equal. Sub-project 2 uses the same asserted-equal-duplicate
/// pattern for <c>EdgeVectorData.QueryGeneratorServiceKey</c> and <c>EdgeEmbeddings.QueryServiceKey</c>.
/// </summary>
/// <remarks>
/// This is the one test class in the repo that sees both packages, which is why the
/// <c>Qavren.Edge.Rag</c> project reference lands in this project and nowhere else (plan
/// adjustment 14).
/// </remarks>
public class CrossPackageLiteralTests
{
    [Fact]
    public void ThePinnedMessageKeyDefaultIsExactlyRagsContextMessagePropertyKey()
    {
        var pinned = new ChatHistoryOptions().PinnedMessageKeys;

        var only = Assert.Single(pinned);
        Assert.Equal(RagCitations.ContextMessagePropertyKey, only);
    }

    [Fact]
    public void TheLiteralIsTheOneBothPackagesDocument()
    {
        // Plain string equality against the literal itself, so a "fix" that renames one side and
        // then updates the other side's test to match is still caught by the third leg.
        Assert.Equal("qavren.edge.rag.context", RagCitations.ContextMessagePropertyKey);
        Assert.Equal("qavren.edge.rag.context", new ChatHistoryOptions().PinnedMessageKeys[0]);
    }
}
