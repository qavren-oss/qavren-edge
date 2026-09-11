using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite.Native.Cipher;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Sqlite.Cipher.Tests;

/// <summary>
/// Spec 16.2 (Cipher) / plan task 5.3: the whole collection lifecycle - create, upsert, KNN,
/// hybrid, delete, drop - over a <b>keyed</b> SQLCipher connection.
/// </summary>
/// <remarks>
/// This is the thing the upstream connector structurally cannot do. It builds its own
/// <c>SqliteConnection</c> from a connection string and calls <c>LoadExtension("vec0")</c> on every
/// open, which sub-project 1's <c>SQLITE_OMIT_LOAD_EXTENSION</c> build forbids and which no cipher
/// key ever reaches. The provider under test rides on <see cref="IEdgeDatabase"/> instead, so the
/// key, the pragmas and the pool are someone else's contract and the same provider code runs
/// unchanged. The last test reopens with the WRONG key and asserts
/// <see cref="EdgeDatabaseKeyException"/>, so the file is proved to be genuinely encrypted rather
/// than silently plain.
/// </remarks>
public class EncryptedVectorStoreTests
{
    private const string Collection = "cipher_notes";

    private static readonly byte[] VectorKey = [.. Enumerable.Range(41, 32).Select(i => (byte)i)];
    private static readonly byte[] OtherKey = [.. Enumerable.Range(200, 32).Select(i => (byte)i)];

    private static readonly float[] UnitX = [1f, 0f, 0f, 0f];
    private static readonly float[] UnitY = [0f, 1f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool NativePresent =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "qedge_sqlcipher.dll")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.so")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.dylib"));

    [Fact]
    public async Task TheFullCollectionLifecycleRunsOverAKeyedConnection()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine; run build-windows.ps1 -Cipher.");

        var root = NewRoot();
        var sp = await StartAsync(root, SqliteKey.FromRawBytes(VectorKey)).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var database = sp.GetRequiredService<IEdgeDatabase>();
            Assert.True(database.IsEncrypted);

            var collection = new EdgeVectorStoreCollection<string, EncryptedNote>(database, Collection);

            // CREATE.
            Assert.False(await collection.CollectionExistsAsync(Token).ConfigureAwait(true));
            await collection.EnsureCollectionExistsAsync(Token).ConfigureAwait(true);
            Assert.True(await collection.CollectionExistsAsync(Token).ConfigureAwait(true));

            // UPSERT. Pre-computed vectors: no generator is anywhere on this path, so every
            // assertion below is about the cipher connection and the provider, nothing else.
            await collection.UpsertAsync(
                [
                    new EncryptedNote { Key = "a", Text = "harbour lantern glow", Embedding = UnitX },
                    new EncryptedNote { Key = "b", Text = "mountain trail dust", Embedding = UnitY },
                ],
                Token).ConfigureAwait(true);

            Assert.Equal(2L, await CountAsync(database, Collection).ConfigureAwait(true));
            Assert.Equal(2L, await CountAsync(database, Collection + "_vec").ConfigureAwait(true));

            var fetched = await collection.GetAsync("a", cancellationToken: Token).ConfigureAwait(true);
            Assert.NotNull(fetched);
            Assert.Equal("harbour lantern glow", fetched.Text);

            // KNN. vec0 returns a DISTANCE, so lower is better and the nearest comes first.
            var knn = await collection
                .SearchAsync(new ReadOnlyMemory<float>(UnitY), top: 2, cancellationToken: Token)
                .ToListAsync(Token).ConfigureAwait(true);

            Assert.Equal(["b", "a"], knn.Select(r => r.Record.Key));
            Assert.True(knn[0].Score < knn[1].Score, "vec0 returns a distance, so lower is better.");

            // HYBRID. The same query vector still ranks "b" first in the vector lane, but only "a"
            // matches the keyword lane, so RRF at k = 60 fuses to 1/61 + 1/62 for "a" against
            // 1/61 for "b" and the order INVERTS. A hybrid search that silently degraded to plain
            // KNN - which is what a broken FTS lane over a keyed connection would look like -
            // would return "b" first and fail here.
            var hybrid = await collection
                .HybridSearchAsync(new ReadOnlyMemory<float>(UnitY), ["lantern"], top: 2, cancellationToken: Token)
                .ToListAsync(Token).ConfigureAwait(true);

            Assert.Equal(["a", "b"], hybrid.Select(r => r.Record.Key));
            Assert.True(hybrid[0].Score > hybrid[1].Score, "RRF returns a similarity, so higher is better.");

            // DELETE. The sidecar row goes with it, and it goes by TRIGGER, not by provider code.
            await collection.DeleteAsync("a", Token).ConfigureAwait(true);
            Assert.Null(await collection.GetAsync("a", cancellationToken: Token).ConfigureAwait(true));
            Assert.Equal(1L, await CountAsync(database, Collection).ConfigureAwait(true));
            Assert.Equal(1L, await CountAsync(database, Collection + "_vec").ConfigureAwait(true));

            // DROP.
            await collection.EnsureCollectionDeletedAsync(Token).ConfigureAwait(true);
            Assert.False(await collection.CollectionExistsAsync(Token).ConfigureAwait(true));
        }

        SqliteConnection.ClearAllPools();
        Delete(root);
    }

    [Fact]
    public async Task TheVectorDataIsOnDiskEncryptedAndTheWrongKeyCannotOpenIt()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var path = Path.Combine(root, "secret.db");

        var sp = await StartAsync(root, SqliteKey.FromRawBytes(VectorKey)).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var collection = new EdgeVectorStoreCollection<string, EncryptedNote>(
                sp.GetRequiredService<IEdgeDatabase>(),
                Collection);

            await collection.EnsureCollectionExistsAsync(Token).ConfigureAwait(true);
            await collection.UpsertAsync(
                new EncryptedNote { Key = "a", Text = "harbour lantern glow", Embedding = UnitX },
                Token).ConfigureAwait(true);
        }

        SqliteConnection.ClearAllPools();

        // A plain SQLite file starts with the 16-byte magic. A SQLCipher file encrypts page 1
        // header and all, so the magic is absent: the vec0 blobs and the FTS5 index that the
        // lifecycle test just wrote are ciphertext on disk.
        var header = new byte[16];
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using (stream.ConfigureAwait(true))
        {
            Assert.Equal(16, await stream.ReadAsync(header, Token).ConfigureAwait(true));
        }

        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);

        // And the key is what gates it: startup reads sqlite_master, so the wrong key is rejected
        // before EnsureStartedAsync ever returns a usable provider.
        await Assert.ThrowsAsync<EdgeDatabaseKeyException>(async () =>
        {
            var wrong = await StartAsync(root, SqliteKey.FromRawBytes(OtherKey)).ConfigureAwait(true);
            await wrong.DisposeAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        SqliteConnection.ClearAllPools();
        Delete(root);
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

    private static async Task<ServiceProvider> StartAsync(string root, SqliteKey key)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o =>
            {
                o.DatabaseName = "secret.db";
                o.Directory = root;
                o.Key = key;
            })
            .UseSqliteNativeCipher());

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).ConfigureAwait(false);
        return sp;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-cipher-vector", Guid.NewGuid().ToString("N"));
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
    /// A pre-computed vector plus one full-text column: enough for KNN and for the FTS5 lane of a
    /// hybrid search, with no embedding generator in the process.
    /// </summary>
    public sealed class EncryptedNote
    {
        /// <summary>The key.</summary>
        [VectorStoreKey]
        public string Key { get; set; } = "";

        /// <summary>The keyword lane's only column.</summary>
        [VectorStoreData(IsFullTextIndexed = true)]
        public string Text { get; set; } = "";

        /// <summary>A four-wide pre-computed vector.</summary>
        [VectorStoreVector(4, DistanceFunction = DistanceFunction.CosineDistance)]
        public ReadOnlyMemory<float> Embedding { get; set; }
    }

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
