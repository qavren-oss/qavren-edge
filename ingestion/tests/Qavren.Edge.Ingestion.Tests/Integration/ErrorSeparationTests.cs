using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Onnx;
using Qavren.Edge.Ingestion.OpenXml;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Qavren.Edge.Onnx;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 13.3's separations and spec 11.1's startup checks at integration scope: 6206 against
/// 6207 by call site, 6006 and 6011 before any write, the two size-gate enforcement points, the
/// non-seekable ceiling, and spec 4.3's snippet resolved from a real container in both
/// builder-call orders. Task 7.1 Step 6.
/// </summary>
public sealed class ErrorSeparationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RecordingSource ThreeDocuments() =>
        new RecordingSource().Add("a.txt", "alpha document").Add("b.txt", "beta document").Add("c.txt", "gamma document");

    /// <summary>
    /// 6206: the embed threw. The window is halved and retried ONCE; when the retry fails too the
    /// run suspends with <c>embed:failed</c> and the failure carries the code. Two documents were
    /// committed before it, and the resume finishes the third.
    /// </summary>
    [Fact]
    public async Task A_generator_that_throws_on_the_third_call_is_6206_one_halved_retry_then_Suspended()
    {
        using var host = await IntegrationHost.StartAsync();
        var calls = 0;
        host.Generator.OnCall = () =>
        {
            if (++calls == 3)
            {
                host.Generator.FailAlways = new InvalidOperationException("the model is gone");
            }
        };

        var result = await host.Pipeline.RunAsync(ThreeDocuments(), cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("embed:failed", result.SuspendReason);
        Assert.NotNull(result.Failure);
        Assert.Equal(EdgeErrorCode.IngestionEmbeddingFailed, result.Failure.Code);
        Assert.Equal(2, result.DocumentsIndexed);

        // Exactly one halved retry: three calls plus the single retried half of a one-chunk window.
        Assert.Equal(4, host.Generator.CallCount);
        var shrunk = Assert.Single(host.Logs.Lines, l => l.StartsWith("918|", StringComparison.Ordinal));
        Assert.Contains("embed:failed", shrunk, StringComparison.Ordinal);

        // The two committed documents are on disk; the third has no state row and re-runs cleanly.
        Assert.Equal(2, await Db.CountAsync(host.Database, Db.DocumentTable));
        host.Generator.FailAlways = null;
        host.Generator.OnCall = null;

        var resumed = await host.Pipeline.RunAsync(ThreeDocuments(), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, resumed.Outcome);
        Assert.Equal(1, resumed.DocumentsIndexed);
        Assert.Equal(1, resumed.EmbedCalls);
    }

    /// <summary>
    /// 6207: the write threw, not the embed, so nothing is retried. The generator was called once,
    /// there is no 918, and the failure is document-tier with the code on it.
    /// </summary>
    [Fact]
    public async Task A_collection_whose_upsert_throws_is_6207_with_no_retry()
    {
        FaultingEdgeDatabase? faulting = null;
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            WrapDatabase = inner => faulting = new FaultingEdgeDatabase(inner) { FailOnArmedTransaction = 1 },
        });
        Assert.NotNull(faulting);

        // The first transaction after the embed call is SP2's UpsertAsync.
        host.Generator.OnCall = faulting.Arm;

        var result = await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha document"), cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        var failed = Assert.Single(result.Failures);
        Assert.Equal(EdgeErrorCode.IngestionWriteFailed, failed.Failure!.Code);
        Assert.Equal(1, faulting.Faults);
        Assert.Equal(1, host.Generator.CallCount);
        Assert.DoesNotContain(host.Logs.Lines, l => l.StartsWith("918|", StringComparison.Ordinal));
        Assert.Equal(0, await Db.CountAsync(host.Database, Db.DataTable));
    }

    [Fact]
    public async Task A_dimension_mismatch_is_6006_at_startup_before_any_write()
    {
        var root = IntegrationHost.NewRoot();
        try
        {
            using var provider = IntegrationHost.Build(
                new IntegrationHostOptions { Root = root, Generator = new RecordingEmbeddingGenerator(dimensions: 768) },
                out _,
                out _);

            var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
                () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

            Assert.Equal(EdgeErrorCode.IngestionCollectionDimensionMismatch, error.Code);
            Assert.Equal(0, RawRowCount(root, Db.DataTable));
            Assert.Equal(0, RawRowCount(root, Db.DocumentTable));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            IntegrationHost.DeleteRoot(root);
        }
    }

    /// <summary>
    /// Spec 9.1's second enforcement point: the source declared no size, so the ceiling is met by
    /// the counted read. Recorded 6052 with NO content hash, because the hash of a truncated read
    /// would be a lie - and the shrunk file then ingests on the next run.
    /// </summary>
    [Fact]
    public async Task An_undeclared_size_over_MaxDocumentBytes_is_6052_from_the_counted_read_with_no_hash_written()
    {
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            Ingestion = o => o.MaxDocumentBytes = 1024,
        });

        var content = Encoding.UTF8.GetBytes(Prose.Document(4, 100));
        Assert.True(content.Length > 1024);
        var opens = 0;
        var source = IngestionSource.Single(
            "big.txt",
            IngestionMediaTypes.PlainText,
            _ =>
            {
                opens++;
                return new ValueTask<Stream>(new MemoryStream(content, writable: false));
            },
            "undeclared");

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        var failed = Assert.Single(first.Failures);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, failed.Failure!.Code);
        Assert.Equal(1, opens);
        Assert.Equal(1, await Db.CountAsync(host.Database, Db.DocumentTable));
        Assert.Null(await Db.DocumentContentHashAsync(host.Database, "big.txt"));
        Assert.Equal(0, await Db.CountAsync(host.Database, Db.DataTable));

        // The file shrinks under the ceiling: the next run ingests it.
        content = Encoding.UTF8.GetBytes("now it is small");
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Empty(second.Failures);
        Assert.Equal(1, second.DocumentsIndexed);
        Assert.NotNull(await Db.DocumentContentHashAsync(host.Database, "big.txt"));
        Assert.Equal(1, await Db.CountAsync(host.Database, Db.DataTable));
    }

    /// <summary>
    /// Spec 7.1's seekability contract through the whole pipeline: a small non-seekable stream is
    /// buffered once per open and ingests; one over the ceiling is 6053 without being drained.
    /// </summary>
    [Fact]
    public async Task Non_seekable_streams_ingest_when_small_and_are_6053_without_being_drained_when_large()
    {
        const int Limit = 4096;
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            Ingestion = o => o.Extraction.NonSeekableBufferLimitBytes = Limit,
        });

        var small = Encoding.UTF8.GetBytes("first paragraph\n\nsecond paragraph\n");
        var large = new byte[2 * 1024 * 1024];
        Array.Fill(large, (byte)'a');
        var largeStreams = new List<CountingNonSeekableStream>();

        var source = IngestionSource.Items(
            [
                new DocumentSourceItem("small.txt", IngestionMediaTypes.PlainText, _ => new ValueTask<Stream>(new CountingNonSeekableStream(small))),
                new DocumentSourceItem("large.txt", IngestionMediaTypes.PlainText, _ =>
                {
                    var stream = new CountingNonSeekableStream(large);
                    largeStreams.Add(stream);
                    return new ValueTask<Stream>(stream);
                }),
            ],
            "handles");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        var byId = result.Documents.ToDictionary(d => d.DocumentId, StringComparer.Ordinal);
        Assert.Equal(IngestionDocumentOutcome.Indexed, byId["small.txt"].Outcome);
        Assert.True(byId["small.txt"].ChunksAdded >= 1, "the small non-seekable document produced no chunk");
        Assert.Equal(IngestionDocumentOutcome.Failed, byId["large.txt"].Outcome);
        Assert.Equal(EdgeErrorCode.IngestionDocumentUnreadable, byId["large.txt"].Failure!.Code);

        // Never read to the end: at most one buffer's overshoot past the limit, on every open.
        Assert.NotEmpty(largeStreams);
        Assert.All(largeStreams, s => Assert.True(s.BytesRead <= Limit + Internal.SourceStream.BufferSize, $"read {s.BytesRead} bytes"));
        Assert.All(largeStreams, s => Assert.True(s.BytesRead < large.Length));
    }

    /// <summary>
    /// Spec 11.1's 6011, at startup and before any write, over the three shaping values whose
    /// divergence the DDL comparison must catch - and the matching configuration then starts clean
    /// and ingests.
    /// </summary>
    [Theory]
    [InlineData("ChunkSize")]
    [InlineData("VectorTableNameFormat")]
    [InlineData("FullTextRemoveDiacritics")]
    public async Task A_collection_shaping_divergence_is_6011_at_startup_before_any_write(string knob)
    {
        Action<EdgeVectorStoreOptions> store = knob switch
        {
            "ChunkSize" => o => o.ChunkSize = 512,
            "VectorTableNameFormat" => o => o.VectorTableNameFormat = "{0}_vectors",
            _ => o => o.FullTextRemoveDiacritics = 1,
        };
        Action<IngestionOptions> matching = knob switch
        {
            "ChunkSize" => o => o.ConfigureCollection = c => c.ChunkSize = 512,
            "VectorTableNameFormat" => o => o.ConfigureCollection = c => c.VectorTableName = "chunks_vectors",
            _ => o => o.FullTextRemoveDiacritics = 1,
        };
        var vectorTable = knob == "VectorTableNameFormat" ? "chunks_vectors" : Db.VectorTable;

        var root = IntegrationHost.NewRoot();
        try
        {
            using (var mismatched = IntegrationHost.Build(new IntegrationHostOptions { Root = root, Store = store }, out _, out _))
            {
                var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
                    () => mismatched.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

                Assert.Equal(EdgeErrorCode.IngestionCollectionSchemaMismatch, error.Code);
                Assert.Contains("CREATE VIRTUAL TABLE", error.Message, StringComparison.Ordinal);
            }

            // Before any write: the migrations ran below order 400, the chunk and state tables are
            // empty, and nothing was embedded.
            SqliteConnection.ClearAllPools();
            Assert.Equal(0, RawRowCount(root, Db.DataTable));
            Assert.Equal(0, RawRowCount(root, Db.DocumentTable));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            IntegrationHost.DeleteRoot(root);
        }

        // The matching configuration, on a fresh database, starts clean and ingests: the shaping
        // values agree, so the tables the migration creates are the ones the runtime writes to.
        using var matched = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            Store = store,
            Ingestion = matching,
        });

        var result = await matched.Pipeline.RunAsync(ThreeDocuments(), cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.ChunksAdded);
        Assert.Equal(3, await Db.CountAsync(matched.Database, vectorTable));
    }

    /// <summary>
    /// Spec 4.3's snippet from a real <see cref="ServiceCollection"/>, end to end - ingest, then
    /// SP2's hybrid search over the same store - in both builder-call orders, so it cannot regress
    /// into <c>EmbeddingGeneratorMissing</c>: the same guard SP2 spec 16.2 has.
    /// <para>
    /// <c>AddOnnxEmbeddings</c> is stood in for by <c>AddOnnx</c> (the resource monitor and model
    /// paths it would register) plus a generator that answers the two <c>GetService</c> routes
    /// <c>AddOnnxIngestion</c> reads - the preset and the tokenizer - and embeds through the
    /// recording generator. No model is downloaded and no ORT session runs (plan adjustment 27).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_spec_4_3_snippet_resolves_end_to_end_from_a_real_container_in_both_orders(bool snippetOrder)
    {
        var root = IntegrationHost.NewRoot();
        Directory.CreateDirectory(root);
        var generator = new PresetPublishingGenerator(EmbeddingPresets.MiniLmL6V2Int8);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new ScratchPaths(root));

        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "notes.db";
                o.Directory = root;
            });

            if (snippetOrder)
            {
                StandInForAddOnnxEmbeddings(edge, generator);
                edge.AddVectorStore();
                edge.AddIngestion(migrationVersion: 10);
                edge.AddOnnxIngestion();
                edge.AddPdfExtractor();
                edge.AddDocxExtractor();
            }
            else
            {
                edge.AddDocxExtractor();
                edge.AddPdfExtractor();
                edge.AddOnnxIngestion();
                edge.AddIngestion(migrationVersion: 10);
                edge.AddVectorStore();
                StandInForAddOnnxEmbeddings(edge, generator);
            }
        });

        try
        {
            using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token);

            var pipeline = provider.GetRequiredService<IIngestionPipeline>();
            const string Markdown =
                "# Roof\n\nWater is coming through the roof after the storm; the leak is over the kitchen.\n\n" +
                "# Garden\n\nThe gutter is blocked with leaves and needs clearing before winter.\n";
            var bytes = Encoding.UTF8.GetBytes(Markdown);

            var result = await pipeline.RunAsync(
                IngestionSource.Single(
                    "house.md",
                    IngestionMediaTypes.Markdown,
                    _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)),
                    sourceId: "notes"),
                cancellationToken: Token);

            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
            Assert.Equal(1, result.DocumentsIndexed);
            Assert.Empty(result.Failures);

            // Two short sections merge forward under MinTokens (event 911), so this is "at least
            // one chunk, embedded through the generator the snippet resolved" - not a chunk count.
            Assert.True(result.ChunksAdded >= 1, $"expected at least one chunk, got {result.ChunksAdded}");
            Assert.Equal(result.EmbedCalls, generator.Inner.CallCount);
            Assert.True(generator.Inner.CallCount > 0, "the snippet's generator was never called");

            // The extractors and the bridge are live registrations, whichever order they were chained in.
            var extractors = provider.GetRequiredService<IDocumentExtractorRegistry>().Describe();
            Assert.Contains("pdf:", extractors, StringComparison.Ordinal);
            Assert.Contains("docx:", extractors, StringComparison.Ordinal);
            Assert.IsType<EdgeChunkTokenizer>(provider.GetRequiredService<IChunkTokenizer>());
            Assert.IsType<ResourceMonitorThrottle>(provider.GetRequiredService<IIngestionThrottle>());

            // Search is SP2's, unchanged: the snippet's second half, verbatim in shape.
            var store = provider.GetRequiredService<EdgeVectorStore>();
            var chunks = store.GetDynamicCollection(
                "chunks", IngestionSchema.BuildDefinition(dimensions: 384, DistanceFunction.CosineDistance, fullTextIndexed: true));

            var hits = await chunks
                .HybridSearchAsync(
                    "water damage",
                    ["roof", "leak"],
                    top: 10,
                    new HybridSearchOptions<Dictionary<string, object?>> { AdditionalProperty = r => r[IngestionColumns.Text] },
                    Token)
                .ToListAsync(Token);

            Assert.NotEmpty(hits);
            var best = IngestedChunk.FromRecord(hits[0].Record);
            Assert.Equal("house.md", best.DocumentId);
            Assert.Contains("roof", best.Text, StringComparison.OrdinalIgnoreCase);
            Assert.All(hits, h => Assert.True(h.Score > 0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            IntegrationHost.DeleteRoot(root);
        }
    }

    /// <summary>
    /// What <c>AddOnnxEmbeddings</c> would leave behind that this test needs: SP2's L0
    /// registrations (resource monitor, model paths), and a generator under BOTH the closed
    /// generic and the non-generic <see cref="IEmbeddingGenerator"/> - SP2's store resolves the
    /// latter, and a snippet that only satisfied the former would still throw at search time.
    /// </summary>
    private static void StandInForAddOnnxEmbeddings(EdgeBuilder edge, PresetPublishingGenerator generator)
    {
        edge.AddOnnx();
        edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
        edge.Services.AddSingleton<IEmbeddingGenerator>(generator);
    }

    /// <summary>A row count over the raw file, or 0 when the table was never created. No host in the way.</summary>
    private static long RawRowCount(string root, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "integration.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
        exists.Parameters.AddWithValue("$n", table);
        if ((long)exists.ExecuteScalar()! == 0)
        {
            return 0;
        }

        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return (long)count.ExecuteScalar()!;
    }

    private sealed class ScratchPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}

/// <summary>
/// The two <c>GetService</c> routes <c>AddOnnxIngestion</c> reads off the real
/// <c>OnnxEmbeddingGenerator</c> - the preset and the tokenizer - plus SP2's query-sibling key,
/// over a <see cref="RecordingEmbeddingGenerator"/> that does the embedding.
/// </summary>
internal sealed class PresetPublishingGenerator(EmbeddingPreset preset) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly WhitespaceEdgeTokenizer _tokenizer = new(preset.MaxSequenceLength);

    public RecordingEmbeddingGenerator Inner { get; } = new(preset.Dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Inner.GenerateAsync(values, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            // SP2's store asks the document generator for its query sibling by this key. MiniLM
            // has no prefixes, so the sibling IS this generator.
            return string.Equals(serviceKey as string, EdgeVectorData.QueryGeneratorServiceKey, StringComparison.Ordinal)
                && serviceType.IsInstanceOfType(this)
                ? this
                : null;
        }

        if (serviceType == typeof(EmbeddingPreset))
        {
            return preset;
        }

        if (serviceType == typeof(IEdgeTokenizer))
        {
            return _tokenizer;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata("preset-recording", defaultModelDimensions: preset.Dimensions);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() => Inner.Dispose();
}

/// <summary>
/// A whitespace <see cref="IEdgeTokenizer"/> for the bridge to wrap. Only <see cref="CountTokens"/>
/// is real: <see cref="EdgeChunkTokenizer"/> delegates <c>IndexByTokenCount</c> to the core's
/// <c>TokenIndexSearch</c> (plan adjustment 1), and encoding never happens here.
/// </summary>
internal sealed class WhitespaceEdgeTokenizer(int maxSequenceLength) : IEdgeTokenizer
{
    public EdgeTokenizerKind Kind => EdgeTokenizerKind.WordPieceVocabTxt;

    public int VocabularySize => 1;

    public int MaxSequenceLength => maxSequenceLength;

    public int PadTokenId => 0;

    public int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed) =>
        throw new NotSupportedException("Nothing in this test encodes.");

    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets) =>
        throw new NotSupportedException("Nothing in this test encodes.");

    public int CountTokens(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount) =>
        throw new NotSupportedException("The bridge must never call this (plan adjustment 1).");

    public void Dispose()
    {
    }
}
