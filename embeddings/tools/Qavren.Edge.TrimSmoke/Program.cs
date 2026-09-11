using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.Tests.Fixtures;
using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.TrimSmoke;

/// <summary>
/// What a trimmed - and, on the second leg, an AOT - publish of this stack actually does. Spec 19
/// item 12 is a measured fact rather than an opinion: neither ONNX Runtime's nor
/// <c>Microsoft.ML.Tokenizers</c>'s managed assemblies carry <c>IsAotCompatible</c>, trim-analysis
/// attributes or ILLink descriptors, so what survives a trimmer cannot be inferred from the
/// packages. This program is what decides what the README is allowed to claim.
/// <para>
/// <b>It goes through <see cref="EdgeDynamicVectorStoreCollection"/> and nothing else.</b> The
/// collection is reached with <c>GetDynamicCollection</c> and a
/// <see cref="MEVD.VectorStoreCollectionDefinition"/>, never
/// <c>GetCollection&lt;TKey, TRecord&gt;</c>: publishing the dynamic path is the point, because it
/// is what turns spec 12.6's "the only trim/AOT-safe path" from an assertion into a measured
/// claim. Nothing here reflects over a record type, an anonymous type, or
/// <c>SqliteConnectionExtensions.ToParameters(object)</c> - SP1 annotates that one
/// <c>RequiresUnreferencedCode</c> precisely because the trimmer deletes what it reflects over.
/// </para>
/// <para>
/// Every stage prints and every stage is checked. A trimmed publish that silently returns an empty
/// result must fail the job rather than pass it quietly, so each check that fails writes to stderr
/// and the process exits non-zero.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>The tier-1 fixture graph's output width, and therefore the collection's.</summary>
    private const int Dimensions = 4;

    /// <summary>
    /// The fixture graph is a single <c>Gather</c> over a 16 x 4 table, so every token id has to be
    /// below 16. <c>b</c>, <c>c</c>, <c>d</c> and <c>e</c> are single-letter entries at ids 6-9 in
    /// the committed vocabulary; <c>a</c> is avoided because the fixture lists it twice.
    /// </summary>
    private const string DocumentOne = "b c d e";

    private const string DocumentTwo = "d e";

    private const string CollectionName = "smoke";

    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-trim-smoke", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);

        try
        {
            return await RunAsync(root, staging).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<int> RunAsync(string root, string staging)
    {
        // 1. The fixture, on disk. Nothing is downloaded and nothing binary is committed: the graph
        //    and the vocabulary are base64 const strings in the LINKED TinyModels.g.cs.
        var graph = Convert.FromBase64String(TinyModels.HiddenStates);
        var graphPath = Path.Combine(staging, "model.onnx");
        await File.WriteAllBytesAsync(graphPath, graph).ConfigureAwait(false);

        var vocabulary = Vocabulary();
        var vocabularyPath = Path.Combine(staging, "vocab.txt");
        await File.WriteAllBytesAsync(vocabularyPath, vocabulary).ConfigureAwait(false);

        Log($"[1/6] staged  graph {graph.Length} B, vocab {vocabulary.Length} B under {staging}");

        var preset = Preset(graph, vocabulary);
        var paths = new ScratchPaths(root);

        var services = new ServiceCollection();
        services.AddLogging();

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first.
        services.AddSingleton<IEdgePaths>(paths);

        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "smoke.db";
                o.Directory = root;
            });
            edge.UseModelPaths(paths);
            edge.AddOnnxEmbeddings(o =>
            {
                o.Preset = preset;
                o.ModelSource = new FileOnnxModelSource(paths, staging);
                o.MaxBatchSize = 4;
            });
            edge.AddVectorStore();
        });

        var provider = services.BuildServiceProvider();
        await using (provider.ConfigureAwait(false))
        {
            await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync().ConfigureAwait(false);
            Log("[2/6] host    started");

            // 2. Tokenize a fixed string, over the real vocabulary file, so Microsoft.ML.Tokenizers
            //    is genuinely in the trimmed graph rather than referenced and dead.
            using var tokenizer = EdgeTokenizer.CreateWordPiece(
                vocabularyPath,
                new WordPieceTokenizerOptions
                {
                    LowerCase = preset.LowerCase,
                    MaxSequenceLength = preset.MaxSequenceLength,
                });

            var ids = new int[preset.MaxSequenceLength];
            var count = tokenizer.Encode(DocumentOne, preset.MaxSequenceLength, ids, out _);
            Log($"[3/6] tokens  {count} ids, vocabulary {tokenizer.VocabularySize} entries: {Join(ids, count)}");

            if (count < 3)
            {
                return Fail($"the tokenizer returned {count} ids for '{DocumentOne}'.");
            }

            // 3. Embed, through a real ORT session over the fixture graph.
            var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var embeddings = await generator.GenerateAsync([DocumentOne, DocumentTwo]).ConfigureAwait(false);

            if (embeddings.Count != 2)
            {
                return Fail($"the generator returned {embeddings.Count} embeddings for 2 inputs.");
            }

            var first = embeddings[0].Vector;
            var second = embeddings[1].Vector;

            if (first.Length != Dimensions)
            {
                return Fail($"the embedding is {first.Length}-dimensional; the preset declares {Dimensions}.");
            }

            if (!IsFinite(first.Span) || !IsFinite(second.Span))
            {
                return Fail($"the embedding of '{DocumentOne}' carries a NaN or an infinity.");
            }

            if (first.Span.SequenceEqual(second.Span))
            {
                return Fail($"'{DocumentOne}' and '{DocumentTwo}' embedded to the same vector.");
            }

            Log($"[4/6] embed   [{Format(first.Span)}] and [{Format(second.Span)}]");

            // 4. Upsert and search THROUGH the dynamic collection. GetDynamicCollection with a
            //    definition, never GetCollection<TKey, TRecord>: this is why the job exists.
            var store = provider.GetRequiredService<EdgeVectorStore>();
            var collection = store.GetDynamicCollection(CollectionName, Definition());
            using (collection)
            {
                await collection.EnsureCollectionExistsAsync().ConfigureAwait(false);

                await collection.UpsertAsync(
                    [
                        Row("doc-1", DocumentOne, first),
                        Row("doc-2", DocumentTwo, second),
                    ]).ConfigureAwait(false);

                Log("[5/6] upsert  2 rows into vec0");

                var hits = new List<MEVD.VectorSearchResult<Dictionary<string, object?>>>();
                await foreach (var hit in collection.SearchAsync(first, top: 2).ConfigureAwait(false))
                {
                    hits.Add(hit);
                }

                if (hits.Count != 2)
                {
                    return Fail($"the search returned {hits.Count} rows; 2 were upserted.");
                }

                var nearest = hits[0].Record["Key"] as string;
                Log($"[6/6] search  {hits.Count} hits, nearest '{nearest}' at distance {hits[0].Score}");

                if (!string.Equals(nearest, "doc-1", StringComparison.Ordinal))
                {
                    return Fail($"the nearest row to its own vector is '{nearest}', not 'doc-1'.");
                }

                if (!string.Equals(hits[0].Record["Text"] as string, DocumentOne, StringComparison.Ordinal))
                {
                    return Fail($"the data column round-tripped as '{hits[0].Record["Text"]}'.");
                }
            }
        }

        Log("trim-smoke: OK");
        return 0;
    }

    /// <summary>
    /// The fixture vocabulary, de-duplicated. <c>TinyModels.VocabTxt</c> lists <c>a</c> twice - once
    /// among the single letters and once among the stop words - and the WordPiece vocabulary reader
    /// builds its dictionary with <c>Add</c>, so the raw fixture throws. Keeping the FIRST
    /// occurrence is what a WordPiece vocabulary means and leaves every id below 50 - the specials
    /// and the single letters this program uses - exactly where the raw fixture put them. The defect
    /// is <c>make_tiny_model.py</c>'s, which this task does not own.
    /// </summary>
    private static byte[] Vocabulary()
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(TinyModels.VocabTxt));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var line in raw.Split('\n'))
        {
            var token = line.TrimEnd('\r');
            if (token.Length != 0 && seen.Add(token))
            {
                kept.Add(token);
            }
        }

        return Encoding.UTF8.GetBytes(string.Join('\n', kept) + "\n");
    }

    /// <summary>
    /// A four-dimension preset over the staged fixture, with one <c>[8]</c> sequence bucket. The
    /// graph declares <c>(input_ids, attention_mask)</c> and nothing else, which is why
    /// <see cref="EmbeddingPreset.TokenTypeIdsName"/> is null.
    /// </summary>
    private static EmbeddingPreset Preset(byte[] graph, byte[] vocabulary) => new()
    {
        Id = "trim-smoke-fixture",
        Manifest = new OnnxModelManifest
        {
            ModelId = "qavren-edge-trim-smoke",
            GraphFile = "model.onnx",
            SpdxLicense = "Apache-2.0",
            Files =
            [
                new OnnxModelFile(
                    "model.onnx",
                    OnnxModelFileRole.Graph,
                    graph.Length,
                    Convert.ToHexStringLower(SHA256.HashData(graph))),
                new OnnxModelFile(
                    "vocab.txt",
                    OnnxModelFileRole.Vocabulary,
                    vocabulary.Length,
                    Convert.ToHexStringLower(SHA256.HashData(vocabulary))),
            ],
        },
        ModelFile = "model.onnx",
        TokenizerFile = "vocab.txt",
        TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
        LowerCase = true,
        Dimensions = Dimensions,
        MaxSequenceLength = 8,
        SequenceBuckets = [8],
        Pooling = EmbeddingPooling.Mean,
        Normalize = true,
        TokenTypeIdsName = null,
    };

    /// <summary>
    /// The record shape, declared rather than reflected. A dynamic collection carries no attributes
    /// to read, which is exactly why this path is the trim-safe one.
    /// </summary>
    private static MEVD.VectorStoreCollectionDefinition Definition() => new()
    {
        Properties =
        [
            new MEVD.VectorStoreKeyProperty("Key", typeof(string)),
            new MEVD.VectorStoreDataProperty("Text", typeof(string)) { IsIndexed = true },
            new MEVD.VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), Dimensions)
            {
                DistanceFunction = MEVD.DistanceFunction.CosineDistance,
            },
        ],
    };

    private static Dictionary<string, object?> Row(string key, string text, ReadOnlyMemory<float> vector)
        => new(StringComparer.Ordinal)
        {
            ["Key"] = key,
            ["Text"] = text,
            ["Embedding"] = vector,
        };

    private static bool IsFinite(ReadOnlySpan<float> vector)
    {
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }

    private static string Join(int[] ids, int count)
    {
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = FormattableString.Invariant($"{ids[i]}");
        }

        return string.Join(", ", parts);
    }

    private static string Format(ReadOnlySpan<float> vector)
    {
        var parts = new string[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            parts[i] = FormattableString.Invariant($"{vector[i]:F4}");
        }

        return string.Join(", ", parts);
    }

    private static void Log(FormattableString message)
        => Console.Out.WriteLine(FormattableString.Invariant(message));

    private static void Log(string message) => Console.Out.WriteLine(message);

    private static int Fail(FormattableString message)
    {
        Console.Error.WriteLine("trim-smoke: FAIL - " + FormattableString.Invariant(message));
        return 1;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A still-mapped WAL file or a locked ORT cache entry must not fail a green run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the scratch directory is under TEMP and the OS reclaims it.
        }
    }

    /// <summary>The app and model roots, pointed at one scratch directory.</summary>
    private sealed class ScratchPaths(string root) : IEdgePaths, IEdgeModelPaths
    {
        public string Data => root;

        public string Cache => Ensure("cache");

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
