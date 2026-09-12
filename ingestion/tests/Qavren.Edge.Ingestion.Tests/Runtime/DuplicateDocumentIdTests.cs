using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Error 6054. Duplicate detection belongs to the <b>runner</b>, not to any source:
/// <c>IngestionSource.Items</c> takes a consumer-supplied enumerable SP3 does not control, and
/// <c>Folder</c> can legitimately produce the same id twice under overlapping patterns.
/// </summary>
public sealed class DuplicateDocumentIdTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_duplicate_document_id_fails_the_second_occurrence_and_keeps_the_first()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new DuplicateSource();

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(3, result.DocumentsSeen);
        Assert.Equal(2, result.DocumentsIndexed);
        Assert.Equal(1, result.DocumentsFailed);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(EdgeErrorCode.IngestionDuplicateDocumentId, failure.Failure!.Code);
        Assert.Equal("a.md", failure.DocumentId);

        // The second a.md never reached the extractor, so the first a.md's chunks stayed intact:
        // two opens, the hash pass and the extraction pass, not four.
        Assert.Equal(2, source.Opens["a.md"]);

        var status = await host.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(2, status.DocumentCount);
    }

    [Fact]
    public async Task Document_ids_are_compared_ordinally_so_case_is_two_documents()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("A.md", "upper").Add("a.md", "lower");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Ids are paths or app-chosen keys; case-folding them would merge two real documents on
        // Linux.
        Assert.Equal(2, result.DocumentsIndexed);
        Assert.Equal(0, result.DocumentsFailed);
    }

    private sealed class DuplicateSource : IngestionSource
    {
        private readonly Dictionary<string, int> _opens = new(StringComparer.Ordinal);

        public override string Id => "dupes";

        public IReadOnlyDictionary<string, int> Opens => _opens;

#pragma warning disable CS1998
        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Item("a.md");
            yield return Item("b.md");
            yield return Item("a.md");
        }
#pragma warning restore CS1998

        private DocumentSourceItem Item(string id)
        {
            var bytes = Encoding.UTF8.GetBytes("content of " + id);
            return new DocumentSourceItem(
                id,
                IngestionMediaTypes.Markdown,
                _ =>
                {
                    _opens[id] = _opens.TryGetValue(id, out var n) ? n + 1 : 1;
                    return new ValueTask<Stream>(new MemoryStream(bytes, writable: false));
                })
            {
                SizeBytes = bytes.Length,
            };
        }
    }
}
