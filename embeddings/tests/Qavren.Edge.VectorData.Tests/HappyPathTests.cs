using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Onnx;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Spec 4.3's four builder calls, resolved from a real container, with a real
/// <c>AddOnnxEmbeddings</c> generator running real ORT inference over the committed tier-1 fixture
/// graph. This is the test that stops the flagship snippet from regressing into
/// <c>EmbeddingGeneratorMissing</c>: nothing in those four calls hands the store a generator, and
/// the store has to find one - and its query sibling - on its own.
/// </summary>
/// <remarks>
/// It runs twice, with <c>AddOnnxEmbeddings</c> before and after <c>AddVectorStore</c>, because the
/// store's factory resolves at RESOLVE time and builder-call order must not matter. It downloads
/// nothing: the model source is a <c>FileOnnxModelSource</c> over a staged copy of the fixture graph,
/// so provisioning, digest verification and session creation are all the product's own code.
/// </remarks>
public sealed class HappyPathTests
{
    private const string QueryPrefix = "query: ";
    private const string DocumentPrefix = "passage: ";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]   // AddOnnxEmbeddings before AddVectorStore
    [InlineData(false)]  // and after - the factory resolves at resolve time, so order must not matter
    public async Task TheFourCallHappyPathResolvesTheGeneratorAndItsQuerySibling(bool embeddingsFirst)
    {
        using var fixture = TinyOnnxModel.Stage(QueryPrefix, DocumentPrefix);
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            // Ahead of AddOnnxEmbeddings, whose own provider registration is a TryAddSingleton: the
            // fixture graph needs no vocabulary asset, and this is also what records which prefix
            // reached the encoder.
            edge.Services.AddSingleton<IEdgeTokenizerProvider>(fixture.Tokenizers);
            edge.UseModelPaths(fixture.ModelPaths);

            if (embeddingsFirst)
            {
                edge.AddOnnxEmbeddings(Configure(fixture));
                edge.AddVectorStore();
            }
            else
            {
                edge.AddVectorStore();
                edge.AddOnnxEmbeddings(Configure(fixture));
            }

            edge.AddVectorCollectionMigration<string, TinyNote>(version: 1, "notes");
        });

        var store = host.Services.GetRequiredService<EdgeVectorStore>();
        Assert.True(await store.CollectionExistsAsync("notes", Token));

        var notes = store.GetCollection<string, TinyNote>("notes");

        // The upsert embeds a STRING source property. Without the DI-resolved generator this line is
        // exactly where EmbeddingGeneratorMissing is thrown.
        await notes.UpsertAsync(
            [
                new TinyNote { Key = "n1", Tag = "home", Title = "Roof leak", Body = "water is coming through the roof" },
                new TinyNote { Key = "n2", Tag = "home", Title = "Gutter", Body = "the gutter is blocked with leaves" },
                new TinyNote { Key = "n3", Tag = "car", Title = "Tyres", Body = "the front tyres are worn" },
            ],
            Token);

        var hits = await notes
            .HybridSearchAsync(
                "water damage",
                ["roof", "leak"],
                top: 3,
                // TinyNote carries spec 4.3's two full-text columns, so the keyword lane has to be
                // named: GetFullTextDataPropertyOrSingle refuses to pick one for the caller.
                new HybridSearchOptions<TinyNote> { AdditionalProperty = n => n.Body },
                Token)
            .ToListAsync(Token);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Record.Key == "n1");
        Assert.All(hits, h => Assert.True(h.Score > 0));

        // The store got the DOCUMENT generator: every body reached the encoder with the preset's
        // document prefix.
        Assert.Contains(DocumentPrefix + "water is coming through the roof", fixture.Tokenizers.Seen);
        Assert.Contains(DocumentPrefix + "the gutter is blocked with leaves", fixture.Tokenizers.Seen);

        // And it got the QUERY SIBLING, which is the half a store that only resolved
        // IEmbeddingGenerator would quietly lose: the search value carries the query prefix.
        Assert.Contains(QueryPrefix + "water damage", fixture.Tokenizers.Seen);
        Assert.DoesNotContain(DocumentPrefix + "water damage", fixture.Tokenizers.Seen);
    }

    [Fact]
    public async Task TheContainersGeneratorExposesTheQuerySiblingTheStoreAsksItFor()
    {
        // The mechanism spec 12.5 describes, asserted on the registration rather than through the
        // store: the sibling is reached by GetService with the NON-GENERIC IEmbeddingGenerator and
        // this package's own service key, and it is a different instance to the document generator.
        using var fixture = TinyOnnxModel.Stage(QueryPrefix, DocumentPrefix);
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            edge.Services.AddSingleton<IEdgeTokenizerProvider>(fixture.Tokenizers);
            edge.UseModelPaths(fixture.ModelPaths);
            edge.AddOnnxEmbeddings(Configure(fixture));
            edge.AddVectorStore();
        });

        var document = host.Services.GetRequiredService<IEmbeddingGenerator>();
        var query = document.GetService(typeof(IEmbeddingGenerator), EdgeVectorData.QueryGeneratorServiceKey);

        Assert.NotNull(query);
        Assert.IsAssignableFrom<IEmbeddingGenerator>(query);
        Assert.NotSame(document, query);

        // AddEmbeddingGenerator registers BOTH descriptors; a store that asked for the closed
        // generic only would still resolve, and one that asked for the non-generic only would too.
        Assert.NotNull(host.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    private static Action<OnnxEmbeddingOptions> Configure(TinyOnnxModel fixture) =>
        options =>
        {
            options.Preset = fixture.Preset;
            options.ModelSource = fixture.Source;
        };
}
