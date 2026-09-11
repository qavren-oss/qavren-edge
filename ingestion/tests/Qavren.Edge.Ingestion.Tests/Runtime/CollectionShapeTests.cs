using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// <see cref="CollectionShapeProjection"/> and error 6011. The DDL check is the guard on a hazard
/// that is pre-existing in SP2 and would first bite a real corpus here: a consumer who writes
/// <c>AddVectorStore(o =&gt; o.ChunkSize = 512)</c> and leaves <c>ConfigureCollection</c> unset
/// gets migration DDL declaring 256 and a runtime collection that believes 512.
/// </summary>
public sealed class CollectionShapeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_projection_escapes_braces_because_the_name_formats_are_composite_format_strings()
    {
        var options = new EdgeVectorStoreCollectionOptions
        {
            VectorTableName = "odd{name}_vec",
            FullTextTableName = "odd{name}_fts",
        };

        var store = CollectionShapeProjection.ToStoreOptions(options, 2);

        Assert.Equal("odd{{name}}_vec", store.VectorTableNameFormat);
        Assert.Equal("odd{{name}}_fts", store.FullTextTableNameFormat);
    }

    [Fact]
    public void The_projection_takes_diacritics_from_IngestionOptions_and_never_from_the_collection()
    {
        // SP2 declares EdgeVectorStoreCollectionOptions.RemoveDiacritics internal, so nothing
        // outside that assembly can set it. Plan adjustment 4 is exactly this.
        var store = CollectionShapeProjection.ToStoreOptions(new EdgeVectorStoreCollectionOptions(), 0);

        Assert.Equal(0, store.FullTextRemoveDiacritics);
    }

    [Fact]
    public void Unset_members_take_the_store_defaults()
    {
        var store = CollectionShapeProjection.ToStoreOptions(new EdgeVectorStoreCollectionOptions(), 2);

        Assert.Equal("{0}_vec", store.VectorTableNameFormat);
        Assert.Equal("{0}_fts", store.FullTextTableNameFormat);
        Assert.Equal(256, store.ChunkSize);
        Assert.Equal(FtsTokenizer.Unicode61, store.FullTextTokenizer);
    }

    [Fact]
    public async Task A_store_ChunkSize_the_collection_does_not_carry_is_6011()
    {
        using var provider = Wire(store => store.ChunkSize = 512, ingestion: null);

        var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.IngestionCollectionSchemaMismatch, error.Code);
        Assert.Contains("ConfigureCollection", error.Message, StringComparison.Ordinal);
        Assert.Contains("FullTextRemoveDiacritics", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_ConfigureCollection_to_match_starts_clean()
    {
        using var provider = Wire(
            store => store.ChunkSize = 512,
            ingestion: o => o.ConfigureCollection = c => c.ChunkSize = 512);

        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask();
    }

    [Fact]
    public async Task A_store_VectorTableNameFormat_the_collection_does_not_carry_is_6011()
    {
        using var provider = Wire(store => store.VectorTableNameFormat = "{0}_vectors", ingestion: null);

        var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.IngestionCollectionSchemaMismatch, error.Code);
    }

    [Fact]
    public async Task FullTextRemoveDiacritics_goes_through_IngestionOptions_rather_than_ConfigureCollection()
    {
        using var mismatched = Wire(store => store.FullTextRemoveDiacritics = 1, ingestion: null);
        var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
            () => mismatched.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());
        Assert.Equal(EdgeErrorCode.IngestionCollectionSchemaMismatch, error.Code);

        using var matched = Wire(
            store => store.FullTextRemoveDiacritics = 1,
            ingestion: o => o.FullTextRemoveDiacritics = 1);
        await matched.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask();
    }

    private static ServiceProvider Wire(
        Action<EdgeVectorStoreOptions> store, Action<IngestionOptions>? ingestion) =>
        IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore(store);
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1, ingestion);
        });
}
