using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion;
using Qavren.Edge.Onnx;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.Tests.Fixtures;
using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;
#if QEDGE_TRIM_SATELLITES
using System.IO.Compression;
using Qavren.Edge.Ingestion.OpenXml;
using Qavren.Edge.Ingestion.Pdf;
#endif

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
/// <b>SP3 (ingestion spec 17 item 4).</b> The same process also registers <c>AddIngestion</c> and
/// runs one committed <b>Markdown</b> document through the pipeline, then reads a chunk back
/// through the dynamic collection. Markdown rather than plain text on purpose: it puts Markdig -
/// the only dependency in the core that declares neither <c>IsTrimmable</c> nor
/// <c>IsAotCompatible</c> - under the trimmer on every PR. When the console is published with
/// <c>-p:QedgeTrimSatellites=true</c>, it additionally registers the PDF and DOCX extractors and
/// ingests one real PDF and one console-assembled DOCX from the embedded fixture parts - the
/// core-only publish neither references, links nor embeds any of that. The switch is a compile-time
/// <c>#if QEDGE_TRIM_SATELLITES</c>, not a runtime probe: <c>AddPdfExtractor</c> and
/// <c>AddDocxExtractor</c> live in assemblies the core-only publish does not reference, so the
/// calls cannot exist in that compilation at all. The csproj maps the MSBuild property to the
/// symbol (<c>DefineConstants</c> under the same <c>QedgeTrimSatellites</c> condition as the two
/// <c>ProjectReference</c>s and the four <c>EmbeddedResource</c>s); without that mapping the
/// satellite publish links both satellites and then silently runs the core-only path.
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

    /// <summary>
    /// The chunk tokenizer's ceiling. Spec 8.1 derives the budget from it - at 64, with overhead 2
    /// and the default 32-token heading-path reserve, the resolved triple is MaxTokens 30,
    /// OverlapTokens 8, MinTokens 16 - and <see cref="ChunkModelProfile.MaxSequenceLength"/> must
    /// equal it or startup raises 6153. It is deliberately NOT the embedding preset's 8: the
    /// preset truncates what it embeds, the chunker sizes what it stores, and the two ceilings are
    /// independent by design.
    /// </summary>
    private const int ChunkSequenceLength = 64;

    /// <summary>
    /// <c>AddIngestion</c> claims this version AND the next (ADR 0012). The console's database has
    /// no other migration, so 1 and 2 are the first two free versions.
    /// </summary>
    private const int IngestionMigrationVersion = 1;

    private const string ChunksCollectionName = "chunks";

    private const string MarkdownDocumentId = "guide.md";

    private const string MarkdownSourceId = "trim-smoke";

    /// <summary>
    /// A heading, a fence and a table - the three Markdig paths the core's extractor walks.
    /// <para>
    /// <b>Every word here is chosen to be ABSENT from the tiny vocabulary.</b> The chunks are
    /// embedded by the real ORT session over the fixture graph, whose <c>Gather</c> has 16 rows, so
    /// every id the embedding tokenizer produces for a chunk's first six word-pieces (the preset
    /// truncates at 8 with <c>[CLS]</c>/<c>[SEP]</c>) has to be below 16. A word the vocabulary
    /// does not contain becomes <c>[UNK]</c> (id 1); the words that would NOT are the vocabulary's
    /// own (<c>roof</c>, <c>leak</c>, <c>water</c>, <c>damage</c>, <c>note</c>, <c>search</c>,
    /// <c>query</c>, <c>document</c>, <c>the</c>, <c>of</c>, <c>and</c>, <c>to</c>, <c>in</c>,
    /// <c>is</c>, <c>it</c>), the single letters <c>l</c>-<c>z</c>, every digit, and any of those
    /// with a <c>##s</c>/<c>##ing</c>/<c>##ed</c> suffix. Punctuation is <c>[UNK]</c> too.
    /// </para>
    /// </summary>
    private const string MarkdownDocument = """
        # Trim smoke guide

        ## Overview

        This console publishes trimmed. Every sentence here tokenizes without surprises.

        ## Example

        ```csharp
        var builder = services.AddQavrenEdge();
        ```

        ## Table

        | Column | Meaning |
        |---|---|
        | alpha | first |
        | beta | second |
        """;

    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-trim-smoke", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);

        try
        {
            return await RunAsync(root, staging).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A smoke test's contract is "any exception is a non-zero exit", stated on stderr.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Fail($"unhandled {ex.GetType().Name}: {ex.Message}");
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

        Log($"[1/9] staged  graph {graph.Length} B, vocab {vocabulary.Length} B under {staging}");

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

            // SP3. UseChunkTokenizer is the ONNX-free construction path (spec 8.1): the same
            // de-duplicated tiny vocabulary, over Microsoft.ML.Tokenizers' BertTokenizer, from a
            // stream - the console downloads nothing. The profile MUST agree with the tokenizer
            // (MaxSequenceLength, 6153) and with the generator (Dimensions, 6010); both are
            // startup-time checks, not first-document surprises.
            edge.UseChunkTokenizer(_ => EdgeTokenCounter.CreateWordPiece(
                new MemoryStream(vocabulary, writable: false), ChunkSequenceLength, lowerCase: preset.LowerCase));
            edge.AddIngestion(IngestionMigrationVersion, o =>
            {
                o.Model = new ChunkModelProfile("trim-smoke-fixture", Dimensions, ChunkSequenceLength, "Mean");
                o.CollectionName = ChunksCollectionName;
            });
#if QEDGE_TRIM_SATELLITES
            edge.AddPdfExtractor();
            edge.AddDocxExtractor();
#endif
        });

        var provider = services.BuildServiceProvider();
        await using (provider.ConfigureAwait(false))
        {
            await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync().ConfigureAwait(false);
            Log("[2/9] host    started");

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
            Log($"[3/9] tokens  {count} ids, vocabulary {tokenizer.VocabularySize} entries: {Join(ids, count)}");

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

            Log($"[4/9] embed   [{Format(first.Span)}] and [{Format(second.Span)}]");

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

                Log("[5/9] upsert  2 rows into vec0");

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
                Log($"[6/9] search  {hits.Count} hits, nearest '{nearest}' at distance {hits[0].Score}");

                if (!string.Equals(nearest, "doc-1", StringComparison.Ordinal))
                {
                    return Fail($"the nearest row to its own vector is '{nearest}', not 'doc-1'.");
                }

                if (!string.Equals(hits[0].Record["Text"] as string, DocumentOne, StringComparison.Ordinal))
                {
                    return Fail($"the data column round-tripped as '{hits[0].Record["Text"]}'.");
                }
            }

            // 5. SP3: one Markdown document through the pipeline. IngestionSource.Single over an
            //    in-memory string; OpenAsync is called twice (hash pass, extraction pass), so it
            //    hands out a fresh stream each time.
            var pipeline = provider.GetRequiredService<IIngestionPipeline>();
            var markdown = Encoding.UTF8.GetBytes(MarkdownDocument);
            var markdownRun = await pipeline.RunAsync(
                IngestionSource.Single(
                    MarkdownDocumentId,
                    IngestionMediaTypes.Markdown,
                    _ => new ValueTask<Stream>(new MemoryStream(markdown, writable: false)),
                    MarkdownSourceId,
                    sizeBytes: markdown.Length)).ConfigureAwait(false);

            Log($"[7/9] ingest  {Describe(markdownRun)}");

            if (Check(markdownRun, "markdown") is { } markdownProblem)
            {
                return Fail($"the Markdown run: {markdownProblem}");
            }

            // 6. Read one chunk back THROUGH the dynamic collection - the same definition
            //    AddIngestion registered, rebuilt with IngestionSchema.BuildDefinition rather than
            //    fished out of the container, which is what a consumer does after a run.
            var chunks = store.GetDynamicCollection(
                ChunksCollectionName,
                IngestionSchema.BuildDefinition(Dimensions, MEVD.DistanceFunction.CosineDistance, fullTextIndexed: true));
            using (chunks)
            {
                var query = await generator.GenerateAsync(["trimmed console"]).ConfigureAwait(false);
                var chunkHits = new List<MEVD.VectorSearchResult<Dictionary<string, object?>>>();
                await foreach (var hit in chunks.SearchAsync(query[0].Vector, top: 8).ConfigureAwait(false))
                {
                    chunkHits.Add(hit);
                }

                if (chunkHits.Count == 0)
                {
                    return Fail($"the chunk collection returned no rows; the run reported {markdownRun.ChunksAdded} added.");
                }

                var chunk = IngestedChunk.FromRecord(chunkHits[0].Record);
                Log($"[8/9] chunk   {chunkHits.Count} hits; nearest is {chunk.DocumentId} #{chunk.Ordinal} breadcrumb '{chunk.Breadcrumb}' chars [{chunk.CharStart}, {chunk.CharEnd}) {chunk.TokenCount} tokens, {chunk.BlockKind}, via '{chunk.ExtractorId}'");

                if (!string.Equals(chunk.DocumentId, MarkdownDocumentId, StringComparison.Ordinal)
                    || !string.Equals(chunk.SourceId, MarkdownSourceId, StringComparison.Ordinal))
                {
                    return Fail($"the chunk belongs to '{chunk.SourceId}/{chunk.DocumentId}', not '{MarkdownSourceId}/{MarkdownDocumentId}'.");
                }

                if (string.IsNullOrEmpty(chunk.Breadcrumb))
                {
                    return Fail($"chunk #{chunk.Ordinal} has no breadcrumb; every block of the document sits under a heading.");
                }

                if (chunk.CharEnd <= chunk.CharStart || chunk.TokenCount <= 0)
                {
                    return Fail($"chunk #{chunk.Ordinal} spans [{chunk.CharStart}, {chunk.CharEnd}) with {chunk.TokenCount} tokens.");
                }
            }

#if QEDGE_TRIM_SATELLITES
            // 7. The satellites: one real PDF (the embedded minimal-text.pdf, byte for byte) and
            //    one DOCX the console assembles from the three embedded run-split parts. Both
            //    extractors were registered above; the media type selects each.
            var pdf = ReadResource("trimsmoke/minimal-text.pdf");
            var pdfRun = await pipeline.RunAsync(
                IngestionSource.Single(
                    "minimal-text.pdf",
                    IngestionMediaTypes.Pdf,
                    _ => new ValueTask<Stream>(new MemoryStream(pdf, writable: false)),
                    "trim-smoke-pdf",
                    sizeBytes: pdf.Length)).ConfigureAwait(false);

            Log($"[9/9] pdf     {pdf.Length} B: {Describe(pdfRun)}");

            if (Check(pdfRun, "pdf") is { } pdfProblem)
            {
                return Fail($"the PDF run: {pdfProblem}");
            }

            var docx = BuildDocx();
            var docxRun = await pipeline.RunAsync(
                IngestionSource.Single(
                    "run-split.docx",
                    IngestionMediaTypes.Docx,
                    _ => new ValueTask<Stream>(new MemoryStream(docx, writable: false)),
                    "trim-smoke-docx",
                    sizeBytes: docx.Length)).ConfigureAwait(false);

            Log($"[9/9] docx    {docx.Length} B: {Describe(docxRun)}");

            if (Check(docxRun, "docx") is { } docxProblem)
            {
                return Fail($"the DOCX run: {docxProblem}");
            }
#else
            Log("[9/9] satellites not linked: core-only publish");
#endif
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

    /// <summary>
    /// Null when the run indexed exactly one document through the named extractor and wrote at
    /// least one chunk; otherwise the sentence that says what it did instead.
    /// </summary>
    private static string? Check(IngestionRunResult run, string extractorId)
    {
        if (run.Outcome != IngestionRunOutcome.Completed)
        {
            return FormattableString.Invariant(
                $"outcome {run.Outcome} ({run.SuspendReason ?? run.Failure?.Message ?? "no reason recorded"}).");
        }

        if (run.Documents.Count != 1)
        {
            return FormattableString.Invariant($"{run.Documents.Count} document results; 1 was submitted.");
        }

        var document = run.Documents[0];
        if (document.Outcome != IngestionDocumentOutcome.Indexed)
        {
            return FormattableString.Invariant(
                $"document '{document.DocumentId}' is {document.Outcome}: {document.Failure?.Message ?? "no failure recorded"}");
        }

        if (!string.Equals(document.ExtractorId, extractorId, StringComparison.Ordinal))
        {
            return FormattableString.Invariant(
                $"document '{document.DocumentId}' went through '{document.ExtractorId}', not '{extractorId}'.");
        }

        if (run.ChunksAdded < 1 || run.EmbedCalls < 1)
        {
            return FormattableString.Invariant(
                $"{run.ChunksAdded} chunks added over {run.EmbedCalls} embed calls; at least one of each was expected.");
        }

        return null;
    }

    private static string Describe(IngestionRunResult run) => FormattableString.Invariant(
        $"{run.Outcome}, {run.DocumentsIndexed} indexed / {run.DocumentsFailed} failed, +{run.ChunksAdded} chunks, {run.EmbedCalls} embed calls, {run.TokensEmbedded} tokens");

#if QEDGE_TRIM_SATELLITES
    /// <summary>
    /// A string-keyed lookup on this assembly's own manifest resources - not reflection over
    /// types, so it raises no IL2xxx; if it ever does, that warning is a finding the README
    /// records, not a reason to change the mechanism.
    /// </summary>
    private static byte[] ReadResource(string name)
    {
        using var source = typeof(Program).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing embedded resource " + name);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// The DOCX, assembled from the three embedded run-split parts. No .docx is committed anywhere
    /// in this repo, and this console references no test project, so DeterministicOpc is out of
    /// reach by design - nothing here compares bytes, so nothing here needs determinism.
    /// </summary>
    private static byte[] BuildDocx()
    {
        var parts = new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml" };
        var asm = typeof(Program).Assembly;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in parts)
            {
                using var src = asm.GetManifestResourceStream("trimsmoke/docx/" + part)
                    ?? throw new InvalidOperationException("missing embedded part " + part);
                var entry = zip.CreateEntry(part, CompressionLevel.Optimal);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }

        return ms.ToArray();
    }
#endif

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
