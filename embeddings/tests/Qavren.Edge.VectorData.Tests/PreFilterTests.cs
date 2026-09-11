using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// The filter is pushed into vec0 as <c>rowid IN (SELECT "_rowid" FROM &lt;data&gt; WHERE ...)</c>,
/// which vec0's <c>BestIndex</c> claims with <c>omit = 1</c> and ANDs into the chunk validity bitmap
/// before any distance is computed. These tests are what tells a true pre-filter from a client-side
/// pass: with 12 matching rows in 500 and <c>top = 10</c>, a post-filter returns whatever survives
/// of the ten nearest OVERALL - three rows here - while a pre-filter returns the ten nearest
/// MATCHING rows, and each test asserts both halves of that.
/// </summary>
public sealed class PreFilterTests
{
    private const int Dimensions = 384;
    private const int CorpusSize = 500;
    private const string Query = "the query body";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFilterMatching12RowsIn500ReturnsTheTenNearestMatchingRows()
    {
        var generator = new DeterministicEmbeddingGenerator(Dimensions);
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host, generator);
        await collection.EnsureCollectionExistsAsync(Token);

        // Twelve keepers, scattered so they are not the twelve nearest by accident.
        var notes = Enumerable.Range(0, CorpusSize)
            .Select(i => new Note
            {
                Key = "k" + i,
                Tag = i % 42 == 7 ? "keep" : "drop",
                Title = "title " + i,
                Body = "body " + i,
            })
            .ToArray();

        Assert.Equal(12, notes.Count(n => n.Tag == "keep"));
        await collection.UpsertAsync(notes, Token);

        var expected = await BruteForceAsync(generator, notes, n => n.Tag == "keep", top: 10);

        var hits = await collection
            .SearchAsync(
                Query,
                top: 10,
                new VectorSearchOptions<Note> { Filter = n => n.Tag == "keep" },
                Token)
            .ToListAsync(Token);

        Assert.Equal(10, hits.Count);
        Assert.All(hits, h => Assert.Equal("keep", h.Record.Tag));
        Assert.Equal(expected, hits.Select(h => h.Record.Key));

        // The other half of the claim: a post-filter could not have produced that list, because
        // the ten nearest rows overall hold nowhere near ten keepers.
        var unfiltered = await collection.SearchAsync(Query, top: 10, cancellationToken: Token).ToListAsync(Token);
        Assert.True(
            unfiltered.Count(h => h.Record.Tag == "keep") < 10,
            "The corpus is meant to make the unfiltered top 10 mostly non-matching.");
    }

    [Fact]
    public async Task AnOrFilterIsAlsoAPreFilter()
    {
        // An OR across two values is the case no vec0 metadata column could express, and it is the
        // whole reason spec 13.1 pushes the filter as rowid IN (...) rather than using the metadata
        // tier: vec0 metadata constraints are ANDed equality/range tests on ONE column each.
        var generator = new DeterministicEmbeddingGenerator(Dimensions);
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host, generator);
        await collection.EnsureCollectionExistsAsync(Token);

        var notes = Enumerable.Range(0, CorpusSize)
            .Select(i => new Note
            {
                Key = "k" + i,
                Tag = i % 83 == 5 ? "alpha" : i % 83 == 11 ? "beta" : "drop",
                Title = "title " + i,
                Body = "body " + i,
            })
            .ToArray();

        Assert.Equal(12, notes.Count(n => n.Tag is "alpha" or "beta"));
        await collection.UpsertAsync(notes, Token);

        var expected = await BruteForceAsync(generator, notes, n => n.Tag is "alpha" or "beta", top: 10);

        var hits = await collection
            .SearchAsync(
                Query,
                top: 10,
                new VectorSearchOptions<Note> { Filter = n => n.Tag == "alpha" || n.Tag == "beta" },
                Token)
            .ToListAsync(Token);

        Assert.Equal(10, hits.Count);
        Assert.All(hits, h => Assert.True(h.Record.Tag is "alpha" or "beta", h.Record.Tag));
        Assert.Equal(expected, hits.Select(h => h.Record.Key));
        Assert.Contains(hits, h => h.Record.Tag == "alpha");
        Assert.Contains(hits, h => h.Record.Tag == "beta");
    }

    [Fact]
    public async Task AFilterMatchingFewerRowsThanTopReturnsOnlyTheMatchingRows()
    {
        var generator = new DeterministicEmbeddingGenerator(Dimensions);
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host, generator);
        await collection.EnsureCollectionExistsAsync(Token);

        var notes = Enumerable.Range(0, CorpusSize)
            .Select(i => new Note
            {
                Key = "k" + i,
                Tag = i == 300 ? "unique" : "drop",
                Title = "title " + i,
                Body = "body " + i,
            })
            .ToArray();

        await collection.UpsertAsync(notes, Token);

        var hits = await collection
            .SearchAsync(
                Query,
                top: 10,
                new VectorSearchOptions<Note> { Filter = n => n.Tag == "unique" },
                Token)
            .ToListAsync(Token);

        var only = Assert.Single(hits);
        Assert.Equal("k300", only.Record.Key);
    }

    private static EdgeVectorStoreCollection<string, Note> Collection(
        VectorTestHost host,
        DeterministicEmbeddingGenerator generator) =>
        new(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions { EmbeddingGenerator = generator });

    /// <summary>
    /// The expected ranking, computed in C# from the same deterministic generator the collection
    /// embedded with. Every vector it produces is unit length, so vec0's cosine distance is exactly
    /// <c>1 - dot</c> and ordering by one orders by the other.
    /// </summary>
    private static async Task<IReadOnlyList<string>> BruteForceAsync(
        DeterministicEmbeddingGenerator generator,
        IReadOnlyList<Note> notes,
        Func<Note, bool> matches,
        int top)
    {
        var candidates = notes.Where(matches).ToArray();
        var embedded = await generator
            .GenerateAsync([Query, .. candidates.Select(n => n.Body)], cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        var query = embedded[0].Vector.Span.ToArray();

        return
        [
            .. candidates
                .Select((note, i) => (note.Key, Distance: 1.0 - Dot(query, embedded[i + 1].Vector.Span.ToArray())))
                .OrderBy(x => x.Distance)
                .Take(top)
                .Select(x => x.Key),
        ];
    }

    private static double Dot(float[] left, float[] right)
    {
        double total = 0;
        for (var i = 0; i < left.Length; i++)
        {
            total += (double)left[i] * right[i];
        }

        return total;
    }
}
