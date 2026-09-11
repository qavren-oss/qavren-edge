using System.Text;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Tests.Fixtures;

namespace Qavren.Edge.Embeddings.Tests.Fakes;

/// <summary>
/// The tier-1 vocabulary, as a tokenizer. It is the committed base64 fixture, so no test here
/// downloads anything or reads a 231 KB bert-base-uncased vocab off disk.
/// </summary>
/// <remarks>
/// <b>The committed fixture carries a duplicate entry and is de-duplicated here.</b>
/// <c>TinyModels.VocabTxt</c> lists <c>a</c> twice - once among the single letters and once among
/// the stop words - and <c>WordPieceTokenizer.LoadVocab</c> builds a <c>Dictionary</c> with
/// <c>Add</c>, so it throws <c>ArgumentException: An item with the same key has already been
/// added. Key: a</c> on the raw fixture. A real <c>bert-base-uncased</c> vocab has no duplicates,
/// so this is a defect in the fixture generator rather than in the tokenizer or in this package;
/// the fix belongs in <c>embeddings/tests/fixtures/make_tiny_model.py</c>, which this task does not
/// own. De-duplicating keeps the FIRST occurrence, which is what a WordPiece vocabulary means, and
/// leaves every id below 50 - the specials and the single letters this suite actually uses -
/// exactly where the raw fixture put them.
/// </remarks>
internal static class TestVocabulary
{
    /// <summary>Entries after de-duplication: the fixture's 64 lines hold 63 distinct tokens.</summary>
    public const int Size = 63;

    /// <summary>The de-duplicated vocabulary's raw bytes, one token per line.</summary>
    public static byte[] Bytes()
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

    /// <summary>Builds a tokenizer over the fixture vocabulary.</summary>
    public static IEdgeTokenizer Tokenizer(int maxSequenceLength = 32, bool lowerCase = true)
    {
        using var stream = new MemoryStream(Bytes(), writable: false);
        return EdgeTokenizer.CreateWordPiece(
            stream,
            new WordPieceTokenizerOptions { MaxSequenceLength = maxSequenceLength, LowerCase = lowerCase });
    }

    /// <summary>Writes the fixture vocabulary to a file and returns its path.</summary>
    public static string WriteTo(string directory, string fileName = "vocab.txt")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Bytes());
        return path;
    }
}
