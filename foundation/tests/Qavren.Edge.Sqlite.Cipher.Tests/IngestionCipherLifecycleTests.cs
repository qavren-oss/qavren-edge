using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion;
using Qavren.Edge.Sqlite.Native.Cipher;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Sqlite.Cipher.Tests;

/// <summary>
/// Spec 14.3's one SQLCipher test (plan Task 7.1 Step 8): the full ingest lifecycle over a KEYED
/// connection. Nothing in Qavren.Edge.Ingestion reads a connection string - suite decision 10 -
/// so the whole pipeline is indifferent to SQLCipher, and this is the test that says so rather
/// than the README. It lives here because SP1 keeps this project out of the device lanes and it
/// is the only one that may reference <c>Qavren.Edge.Sqlite.Native.Cipher</c>.
/// </summary>
/// <remarks>
/// The tokenizer and the generator are local: this project references the ingestion core and
/// nothing above it, so <c>RecordingEmbeddingGenerator</c> and the core's internal
/// <c>TokenIndexSearch</c> are out of reach, and the forty lines each costs are cheaper than a
/// reference to a test project. The three fixtures are embedded copies of the committed corpus.
/// </remarks>
public class IngestionCipherLifecycleTests
{
    private const string Collection = "chunks";
    private const string SourceId = "keyed-corpus";

    private static readonly string[] Fixtures =
    [
        "text/three-paragraphs.txt",
        "text/unicode.txt",
        "markdown/headings.md",
    ];

    private static readonly byte[] Key = [.. Enumerable.Range(60, 32).Select(i => (byte)i)];
    private static readonly byte[] WrongKey = [.. Enumerable.Range(160, 32).Select(i => (byte)i)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool NativePresent =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "qedge_sqlcipher.dll")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.so")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.dylib"));

    [Fact]
    public async Task TheFullIngestLifecycleRunsOverAKeyedConnection()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine; run build-windows.ps1 -Cipher.");

        var root = NewRoot();
        var generator = new CountingEmbeddingGenerator();
        var source = IngestionSource.Items(Fixtures.Select(Item).ToList(), SourceId);

        int chunks;
        var sp = await StartAsync(root, SqliteKey.FromRawBytes(Key), generator).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var database = sp.GetRequiredService<IEdgeDatabase>();
            Assert.True(database.IsEncrypted);
            var pipeline = sp.GetRequiredService<IIngestionPipeline>();

            // INGEST. Three committed fixtures through the real pipeline: chunk rows, vec0 rows,
            // the FTS5 sidecar and the three state tables, all through the keyed connection.
            var first = await pipeline.RunAsync(source, cancellationToken: Token).ConfigureAwait(true);

            Assert.Equal(IngestionRunOutcome.Completed, first.Outcome);
            Assert.Equal(Fixtures.Length, first.DocumentsIndexed);
            Assert.Empty(first.Failures);
            Assert.True(first.ChunksAdded > Fixtures.Length, $"expected a multi-chunk corpus, got {first.ChunksAdded}");
            Assert.True(first.EmbedCalls > 0);
            chunks = first.ChunksAdded;

            Assert.Equal(chunks, await CountAsync(database, Collection).ConfigureAwait(true));
            Assert.Equal(chunks, await CountAsync(database, Collection + "_vec").ConfigureAwait(true));
            Assert.Equal(chunks, await CountAsync(database, Collection + "_fts").ConfigureAwait(true));
            Assert.Equal(Fixtures.Length, await CountAsync(database, "qedge_ingest_document").ConfigureAwait(true));
            Assert.Equal(1L, await CountAsync(database, "qedge_ingest_run").ConfigureAwait(true));
            Assert.Equal(2L, await CountAsync(database, "qedge_ingest_meta").ConfigureAwait(true));

            // RE-RUN unchanged: the hash gate, zero embeds.
            var callsBefore = generator.CallCount;
            var second = await pipeline.RunAsync(source, cancellationToken: Token).ConfigureAwait(true);

            Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
            Assert.Equal(0, second.EmbedCalls);
            Assert.Equal(0, second.ChunksAdded);
            Assert.Equal(Fixtures.Length, second.DocumentsSkipped);
            Assert.Equal(callsBefore, generator.CallCount);

            // REMOVE the source: the sweep takes the chunks (vec0 and FTS5 by trigger) and the rows.
            Assert.Equal(Fixtures.Length, await pipeline.RemoveSourceAsync(SourceId, ct: Token).ConfigureAwait(true));

            Assert.Equal(0L, await CountAsync(database, Collection).ConfigureAwait(true));
            Assert.Equal(0L, await CountAsync(database, Collection + "_vec").ConfigureAwait(true));
            Assert.Equal(0L, await CountAsync(database, Collection + "_fts").ConfigureAwait(true));
            Assert.Equal(0L, await CountAsync(database, "qedge_ingest_document").ConfigureAwait(true));

            var status = await pipeline.GetStatusAsync(ct: Token).ConfigureAwait(true);
            Assert.Equal(0, status.DocumentCount);
            Assert.Equal(0L, status.ChunkCount);
        }

        SqliteConnection.ClearAllPools();

        // READABLE ONLY WITH THE KEY. The file's first sixteen bytes are ciphertext, not SQLite's
        // magic, and the wrong key is rejected at startup before a provider comes back.
        var header = new byte[16];
        var stream = new FileStream(Path.Combine(root, "secret.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using (stream.ConfigureAwait(true))
        {
            Assert.Equal(16, await stream.ReadAsync(header, Token).ConfigureAwait(true));
        }

        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);

        await Assert.ThrowsAsync<EdgeDatabaseKeyException>(async () =>
        {
            var wrong = await StartAsync(root, SqliteKey.FromRawBytes(WrongKey), new CountingEmbeddingGenerator()).ConfigureAwait(true);
            await wrong.DisposeAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        SqliteConnection.ClearAllPools();
        Delete(root);
    }

    private static async Task<ServiceProvider> StartAsync(string root, SqliteKey key, CountingEmbeddingGenerator generator)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge =>
        {
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "secret.db";
                o.Directory = root;
                o.Key = key;
            });
            edge.UseSqliteNativeCipher();
            edge.AddVectorStore();
            edge.UseChunkTokenizer(_ => new WhitespaceChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
            edge.AddIngestion(migrationVersion: 1, o => o.CollectionName = Collection);
        });

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).ConfigureAwait(false);
        return sp;
    }

    private static DocumentSourceItem Item(string fixture)
    {
        var resource = "fixtures/corpus/" + fixture;
        var assembly = typeof(IngestionCipherLifecycleTests).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), resource, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{resource}' is not embedded.");

        byte[] bytes;
        using (var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var documentId = fixture[(fixture.LastIndexOf('/') + 1)..];
        return new DocumentSourceItem(
            documentId,
            IngestionMediaTypes.FromExtension(documentId),
            _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)),
            Path: fixture,
            SizeBytes: bytes.Length);
    }

    private static async Task<long> CountAsync(IEdgeDatabase database, string table)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            return await connection
                .ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"", cancellationToken: Token)
                .ConfigureAwait(true);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-cipher-ingest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A WAL file may still be mapped on Windows; a leftover temp directory is harmless.
        }
    }

    /// <summary>
    /// A whitespace <see cref="IChunkTokenizer"/> shaped like the MiniLM profile: a 256-token
    /// ceiling and an overhead of 2, so the budget resolves to the real 222 / 32 / 27.
    /// </summary>
    private sealed class WhitespaceChunkTokenizer : IChunkTokenizer
    {
        public string Id => "whitespace";

        public int MaxSequenceLength => 256;

        public int SpecialTokenOverhead => 2;

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

        /// <summary>The index just after the <paramref name="maxTokens"/>-th word, or the end.</summary>
        public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
        {
            ArgumentNullException.ThrowIfNull(text);

            tokenCount = 0;
            var inWord = false;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    inWord = false;
                    continue;
                }

                if (!inWord)
                {
                    if (tokenCount == maxTokens)
                    {
                        return i;
                    }

                    tokenCount++;
                    inWord = true;
                }
            }

            return text.Length;
        }
    }

    /// <summary>A deterministic generator that counts its calls, so "zero embeds" is a number.</summary>
    private sealed class CountingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int Dimensions = 384;
        private readonly ConcurrentQueue<int> _calls = new();

        public int CallCount => _calls.Count;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);

            var result = new GeneratedEmbeddings<Embedding<float>>();
            var count = 0;
            foreach (var value in values)
            {
                count++;
                var vector = new float[Dimensions];
                var seed = value.Length == 0 ? 1 : value[0];
                for (var i = 0; i < Dimensions; i++)
                {
                    vector[i] = (float)Math.Sin((seed + i) * 0.001);
                }

                result.Add(new Embedding<float>(vector));
            }

            _calls.Enqueue(count);
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            if (serviceKey is not null)
            {
                return null;
            }

            return serviceType == typeof(EmbeddingGeneratorMetadata)
                ? new EmbeddingGeneratorMetadata("counting", defaultModelDimensions: Dimensions)
                : serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
