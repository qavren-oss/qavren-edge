using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Task 3.1 Step 2: ordinal ordering so a run's document order is stable, forward-slashed relative
/// ids, <c>SizeBytes</c> from <see cref="FileInfo"/>, and 6051 raised BEFORE any document.
/// </summary>
public sealed class FolderSourceTests : IDisposable
{
    private static readonly string[] AllFourIds = ["a.txt", "alpha/one.txt", "b.txt", "zeta/two.md"];
    private static readonly string[] TopLevelIds = ["a.txt", "b.txt"];
    private static readonly string[] MarkdownIds = ["zeta/two.md"];
    private static readonly string[] LazyIds = ["one", "two"];

    private readonly string _root;

    public FolderSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qedge-sp3-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "zeta"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));

        Write(Path.Combine(_root, "b.txt"), "b root\n");
        Write(Path.Combine(_root, "a.txt"), "a root\n");
        Write(Path.Combine(_root, "alpha", "one.txt"), "alpha one\n");
        Write(Path.Combine(_root, "zeta", "two.md"), "# zeta two\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }

    [Fact]
    public async Task DocumentIdsAreForwardSlashedAndOrdinalSorted()
    {
        var source = IngestionSource.Folder(_root, sourceId: "notes");

        var ids = await IdsAsync(source);

        Assert.Equal("notes", source.Id);
        Assert.Equal(AllFourIds, ids);
        Assert.All(ids, id => Assert.False(id.Contains('\\')));
    }

    [Fact]
    public async Task OrderingIsOrdinalRatherThanCultureAware()
    {
        Write(Path.Combine(_root, "Z.txt"), "upper\n");

        var ids = await IdsAsync(IngestionSource.Folder(_root, sourceId: "notes"));

        // Ordinal puts every upper-case letter before every lower-case one; a culture-aware sort
        // would interleave them, and a golden over the folder would move with the machine's locale.
        Assert.True(
            Array.IndexOf(ids, "Z.txt") < Array.IndexOf(ids, "a.txt"),
            string.Join(", ", ids));
    }

    [Fact]
    public async Task NonRecursiveStopsAtTheTopDirectory()
    {
        var ids = await IdsAsync(IngestionSource.Folder(_root, recursive: false, sourceId: "notes"));

        Assert.Equal(TopLevelIds, ids);
    }

    [Fact]
    public async Task TheSearchPatternIsHonoured()
    {
        var ids = await IdsAsync(IngestionSource.Folder(_root, "*.md", sourceId: "notes"));

        Assert.Equal(MarkdownIds, ids);
    }

    [Fact]
    public async Task SizeAndMediaTypeComeFromTheFileSystemAndTheExtension()
    {
        var items = await ItemsAsync(IngestionSource.Folder(_root, "*.md", sourceId: "notes"));

        var item = Assert.Single(items);
        Assert.Equal(11, item.SizeBytes);
        Assert.NotNull(item.LastModifiedUtc);
        Assert.Equal(IngestionMediaTypes.Markdown, item.MediaType);
        Assert.NotNull(item.Path);
    }

    [Fact]
    public void AMissingRootRaisesSixZeroFiveOneBeforeAnyDocument()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var thrown = Assert.Throws<EdgeIngestionException>(() => IngestionSource.Folder(missing, sourceId: "notes"));

        Assert.Equal(EdgeErrorCode.IngestionSourceUnavailable, thrown.Code);
        Assert.NotNull(thrown.Remediation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptySourceIdRaisesSixZeroFiveFive(string? sourceId)
    {
        var paths = new[] { Path.Combine(_root, "a.txt") };

        var thrown = Assert.Throws<EdgeIngestionException>(() => IngestionSource.Files(paths, sourceId!));

        Assert.Equal(EdgeErrorCode.IngestionSourceIdInvalid, thrown.Code);
    }

    [Fact]
    public async Task FilesUsesThePathsItWasGivenAsDocumentIds()
    {
        var path = Path.Combine(_root, "a.txt");
        var paths = new[] { path };

        var ids = await IdsAsync(IngestionSource.Files(paths, "explicit"));

        Assert.Equal(paths, ids);
    }

    [Fact]
    public async Task SingleCarriesTheConsumersHandleUntouched()
    {
        var opened = 0;
        var single = IngestionSource.Single(
            "content://media/42",
            IngestionMediaTypes.PlainText,
            _ =>
            {
                opened++;
                return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("body\n")));
            },
            "picker",
            sizeBytes: 5);

        var items = await ItemsAsync(single);

        var item = Assert.Single(items);
        Assert.Equal("picker", single.Id);
        Assert.Equal("content://media/42", item.DocumentId);
        Assert.Equal(5, item.SizeBytes);
        Assert.Equal(0, opened);

        await OpenAndDisposeAsync(item);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task TheStreamingItemsOverloadIsEnumeratedLazily()
    {
        var enumerated = 0;

        var source = IngestionSource.Items(Produce, "lazy");
        Assert.Equal(0, enumerated);

        var ids = await IdsAsync(source);
        Assert.Equal(LazyIds, ids);
        Assert.Equal(2, enumerated);

        async IAsyncEnumerable<DocumentSourceItem> Produce([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var id in LazyIds)
            {
                ct.ThrowIfCancellationRequested();
                enumerated++;
                yield return new DocumentSourceItem(
                    id, IngestionMediaTypes.PlainText, _ => ValueTask.FromResult(Stream.Null));
                await Task.Yield();
            }
        }
    }

    private static void Write(string path, string content) =>
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

    private static async Task OpenAndDisposeAsync(DocumentSourceItem item)
    {
        var stream = await item.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<string[]> IdsAsync(IngestionSource source)
    {
        var items = await ItemsAsync(source).ConfigureAwait(false);
        return [.. items.Select(i => i.DocumentId)];
    }

    private static async Task<List<DocumentSourceItem>> ItemsAsync(IngestionSource source)
    {
        var items = new List<DocumentSourceItem>();
        await foreach (var item in source
            .EnumerateAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }
}
