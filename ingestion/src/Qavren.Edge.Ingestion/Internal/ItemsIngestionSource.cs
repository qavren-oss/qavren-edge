using System.Runtime.CompilerServices;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The mobile shape behind <c>IngestionSource.Items</c> and
/// <c>IngestionSource.Single</c>. The handle is opaque: a SAF <c>content://</c> URI, a
/// security-scoped bookmark, a MAUI <c>FileResult</c>, a blob in app storage. SP3 never interprets
/// it; the consumer's <see cref="DocumentSourceItem.OpenAsync"/> does (spec 11.3).
/// </summary>
internal sealed class ItemsIngestionSource : IngestionSource
{
    private readonly IReadOnlyList<DocumentSourceItem>? _items;
    private readonly Func<CancellationToken, IAsyncEnumerable<DocumentSourceItem>>? _enumerate;

    private ItemsIngestionSource(
        string id,
        IReadOnlyList<DocumentSourceItem>? items,
        Func<CancellationToken, IAsyncEnumerable<DocumentSourceItem>>? enumerate)
    {
        Id = id;
        _items = items;
        _enumerate = enumerate;
    }

    public override string Id { get; }

    public static ItemsIngestionSource Over(IEnumerable<DocumentSourceItem> items, string sourceId)
    {
        var id = ValidateId(sourceId);
        return new ItemsIngestionSource(id, items.ToArray(), enumerate: null);
    }

    public static ItemsIngestionSource Over(
        Func<CancellationToken, IAsyncEnumerable<DocumentSourceItem>> enumerate, string sourceId)
    {
        var id = ValidateId(sourceId);
        return new ItemsIngestionSource(id, items: null, enumerate);
    }

    public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_items is not null)
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            // Materialised items need no I/O; keeps the iterator honestly async. See FolderIngestionSource.
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        await foreach (var item in _enumerate!(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
