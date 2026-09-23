using System.Text;

namespace Qavren.Edge.Benchmarks.Infrastructure;

/// <summary>
/// Every input the suite measures, generated from a fixed seed. Two runs on two machines measure
/// the same bytes, which is the only thing that makes their numbers comparable at all.
/// </summary>
internal static class Synthetic
{
    /// <summary>MiniLM's width, and therefore the width every SQLite and vector-store row uses.</summary>
    public const int Dimensions = 384;

    /// <summary>The one seed. Change it and every published number is from a different input.</summary>
    public const int Seed = 20260923;

    private static readonly string[] Syllables =
    [
        "ka", "lo", "mi", "ne", "ru", "sa", "te", "vo", "di", "fa", "gu", "ho", "ji", "ze", "po",
        "bra", "cel", "dor", "fen", "gar", "hil", "jun", "kor", "lan", "mar", "nor", "pel", "ros",
    ];

    /// <summary>
    /// 2 000 distinct pseudo-words. Pseudo, not English, so the SQLite FTS5 selectivity is uniform
    /// and known: a 12-word row contains any given word with probability about 0.6%.
    /// </summary>
    public static IReadOnlyList<string> Words { get; } = BuildWords(2_000);

    /// <summary>The FTS5 query term. Mid-list, so nothing about its position is special.</summary>
    public static string SearchTerm => Words[7];

    /// <summary><paramref name="count"/> unit-length vectors, deterministic for a given seed.</summary>
    public static float[][] UnitVectors(int count, int seed)
    {
        var random = new Random(seed);
        var vectors = new float[count][];
        for (var i = 0; i < count; i++)
        {
            vectors[i] = UnitVector(random);
        }

        return vectors;
    }

    /// <summary>One unit-length vector drawn from <paramref name="random"/>.</summary>
    public static float[] UnitVector(Random random)
    {
        var vector = new float[Dimensions];
        double norm = 0;
        for (var d = 0; d < Dimensions; d++)
        {
            var value = (float)((random.NextDouble() * 2.0) - 1.0);
            vector[d] = value;
            norm += value * value;
        }

        var scale = (float)(1.0 / Math.Sqrt(norm));
        for (var d = 0; d < Dimensions; d++)
        {
            vector[d] *= scale;
        }

        return vector;
    }

    /// <summary><paramref name="count"/> pseudo-words joined by spaces.</summary>
    public static string Sentence(Random random, int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i != 0)
            {
                builder.Append(' ');
            }

            builder.Append(Words[random.Next(Words.Count)]);
        }

        return builder.ToString();
    }

    private static string[] BuildWords(int count)
    {
        var random = new Random(Seed);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>(count);
        while (words.Count < count)
        {
            var parts = 2 + random.Next(3);
            var builder = new StringBuilder();
            for (var p = 0; p < parts; p++)
            {
                builder.Append(Syllables[random.Next(Syllables.Length)]);
            }

            var word = builder.ToString();
            if (seen.Add(word))
            {
                words.Add(word);
            }
        }

        return [.. words];
    }
}
