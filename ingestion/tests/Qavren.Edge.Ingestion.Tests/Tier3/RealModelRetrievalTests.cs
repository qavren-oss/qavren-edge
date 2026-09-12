using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Onnx;
using Qavren.Edge.Ingestion.Tests.Integration;
using Qavren.Edge.Onnx;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Tier3;

/// <summary>
/// Spec 14.5, tier 3: the real int8 MiniLM at the HF revision SP2 pins and caches by SHA-256,
/// through the real four-call shape - <c>AddOnnxEmbeddings</c>, <c>AddVectorStore</c>,
/// <c>AddIngestion</c>, <c>AddOnnxIngestion</c> - over a twenty-document prose corpus. A known
/// query's top-3 contains the expected chunk, and the reported score agrees with an independently
/// computed cosine distance to 1e-3. Retrieval QUALITY is not obtainable from a 4-dim fixture model
/// and must never gate a PR, which is why this is guarded on <c>QAVREN_EDGE_TIER3</c> at runtime.
/// </summary>
public sealed class RealModelRetrievalTests
{
    private const double CosineTolerance = 1e-3;

    /// <summary>Twenty short documents on twenty topics. The roof one is the expected hit.</summary>
    private static readonly (string Id, string Text)[] Corpus =
    [
        ("roof.txt", "Water is coming through the roof after last night's storm. The leak is over the kitchen and the ceiling plaster is soft."),
        ("gutter.txt", "The gutters are blocked with leaves again and overflow every time it rains."),
        ("tyres.txt", "The front tyres on the car are worn down to the wear bars and need replacing before the inspection."),
        ("bread.txt", "A sourdough starter needs feeding twice a day at room temperature until it doubles reliably."),
        ("tax.txt", "Quarterly estimated tax payments are due in April, June, September and January."),
        ("garden.txt", "Tomatoes want full sun, deep watering twice a week and a stake before they get heavy."),
        ("laptop.txt", "The laptop battery drains in two hours; the fan runs constantly and the palm rest gets hot."),
        ("piano.txt", "Practice the left hand alone at half tempo before putting the two hands together."),
        ("hike.txt", "The ridge trail gains nine hundred metres in six kilometres and is exposed above the tree line."),
        ("dog.txt", "The puppy needs three short walks a day and a crate that is large enough to stand up in."),
        ("invoice.txt", "Invoice 4471 is thirty days overdue; the client says the purchase order was never raised."),
        ("recipe.txt", "Brown the onions slowly for twenty minutes before adding the garlic and the tinned tomatoes."),
        ("printer.txt", "The printer reports a paper jam in tray two even when the tray is empty."),
        ("lease.txt", "The lease renews automatically unless notice is given ninety days before the end of the term."),
        ("bike.txt", "The rear derailleur skips under load; the cable is probably stretched and the indexing is off."),
        ("passport.txt", "Passport renewals take six to eight weeks; expedited service is available for an extra fee."),
        ("telescope.txt", "Let the telescope cool to the outside temperature for half an hour before observing planets."),
        ("insurance.txt", "The home insurance policy excludes gradual damage but covers a sudden burst pipe."),
        ("fence.txt", "The fence posts have rotted at the base and two panels blew down in the wind."),
        ("coffee.txt", "Grind finer if the espresso runs fast and sour; coarser if it chokes and tastes bitter."),
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(
        Skip = "QAVREN_EDGE_TIER3 or QAVREN_EDGE_MODEL_DIR not set",
        SkipUnless = nameof(Tier3Available.WithModel),
        SkipType = typeof(Tier3Available))]
    public async Task A_known_query_ranks_the_expected_chunk_in_the_top_three_with_a_cosine_score_the_test_can_recompute()
    {
        var staged = Tier3Available.StagedModelDirectory!;
        var root = IntegrationHost.NewRoot();
        Directory.CreateDirectory(root);
        var modelPaths = new ScratchModelPaths(root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new ScratchPaths(root));
        services.AddQavrenEdge(edge => edge
            .UseSqliteNative()
            .AddSqlite(o =>
            {
                o.DatabaseName = "tier3.db";
                o.Directory = root;
            })
            .UseModelPaths(modelPaths)
            .AddOnnxEmbeddings(o =>
            {
                o.Preset = EmbeddingPresets.MiniLmL6V2Int8;

                // Nothing here can download: a FileOnnxModelSource over the staged directory means
                // the HTTP source is never in the container at all (SP2's Tier3Host idiom).
                o.ModelSource = new FileOnnxModelSource(modelPaths, staged);
            })
            .AddVectorStore()
            .AddIngestion(migrationVersion: 1)
            .AddOnnxIngestion());

        try
        {
            using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token);

            var source = IngestionSource.Items(
                Corpus.Select(d =>
                {
                    var bytes = Encoding.UTF8.GetBytes(d.Text);
                    return new DocumentSourceItem(
                        d.Id,
                        IngestionMediaTypes.PlainText,
                        _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)),
                        SizeBytes: bytes.Length);
                }).ToList(),
                "prose");

            var result = await provider.GetRequiredService<IIngestionPipeline>().RunAsync(source, cancellationToken: Token);

            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
            Assert.Equal(Corpus.Length, result.DocumentsIndexed);
            Assert.Empty(result.Failures);

            var store = provider.GetRequiredService<VectorStore>();
            var chunks = store.GetDynamicCollection(
                "chunks", IngestionSchema.BuildDefinition(384, DistanceFunction.CosineDistance, fullTextIndexed: true));

            const string Query = "a leaking roof after a storm";
            var hits = await chunks
                .SearchAsync(Query, top: 3, new VectorSearchOptions<Dictionary<string, object?>> { IncludeVectors = true }, Token)
                .ToListAsync(Token);

            Assert.Equal(3, hits.Count);
            var ranked = hits.Select(h => IngestedChunk.FromRecord(h.Record).DocumentId).ToList();
            Assert.Contains("roof.txt", ranked);
            TestContext.Current.TestOutputHelper?.WriteLine(
                "top-3 for '" + Query + "': " + string.Join(", ", hits.Select(h =>
                    string.Format(CultureInfo.InvariantCulture, "{0} ({1:F4})", IngestedChunk.FromRecord(h.Record).DocumentId, h.Score))));

            // The score is vec0's cosine DISTANCE. Recompute it from the query embedding and the
            // stored vector: MiniLM carries no prefixes, so the document generator embeds the query
            // exactly as the store's query sibling did. 1e-3, not equality - ORT CPU bit-determinism
            // across hosts is not established.
            var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var query = (await generator.GenerateAsync([Query], cancellationToken: Token))[0].Vector;
            foreach (var hit in hits)
            {
                var stored = (ReadOnlyMemory<float>)hit.Record[IngestionColumns.Embedding]!;
                var distance = 1.0 - Cosine(query.Span, stored.Span);
                Assert.NotNull(hit.Score);
                Assert.True(
                    Math.Abs(hit.Score.Value - distance) < CosineTolerance,
                    $"{IngestedChunk.FromRecord(hit.Record).DocumentId}: reported {hit.Score.Value:F6}, recomputed {distance:F6}");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            IntegrationHost.DeleteRoot(root);
        }
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Assert.Equal(a.Length, b.Length);

        double dot = 0, left = 0, right = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            left += (double)a[i] * a[i];
            right += (double)b[i] * b[i];
        }

        var magnitude = Math.Sqrt(left) * Math.Sqrt(right);
        return magnitude == 0 ? 0 : dot / magnitude;
    }

    private sealed class ScratchPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }

    private sealed class ScratchModelPaths(string root) : IEdgeModelPaths
    {
        public string Models => Ensure("models");

        public string OrtCache => Ensure("ort-cache");

        private string Ensure(string leaf)
        {
            var directory = Path.Combine(root, leaf);
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
