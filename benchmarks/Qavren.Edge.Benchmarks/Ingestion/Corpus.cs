using System.Globalization;
using System.Text;
using Qavren.Edge.Benchmarks.Infrastructure;

namespace Qavren.Edge.Benchmarks.Ingestion;

/// <summary>
/// The synthetic ingestion corpus: 200 Markdown documents from one seed. Each has a title, three
/// to five sections of two or three paragraphs, a bullet list, and on alternate documents a table
/// or a code fence - the block kinds the Markdown extractor and the heading chunker walk.
/// </summary>
internal static class Corpus
{
    public const int DocumentCount = 200;

    private static readonly Lazy<IReadOnlyList<byte[]>> Cached = new(Build);

    /// <summary>The documents' UTF-8 bytes, in order. Deterministic.</summary>
    public static IReadOnlyList<byte[]> Documents => Cached.Value;

    /// <summary>File name of document <paramref name="index"/>.</summary>
    public static string FileName(int index) => "doc-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".md";

    /// <summary>
    /// A WordPiece <c>vocab.txt</c> holding the BERT specials, every single character the corpus
    /// uses (and its continuation piece), and every corpus word - so every word is exactly one
    /// token and nothing is <c>[UNK]</c>.
    /// </summary>
    public static byte[] Vocabulary()
    {
        var lines = new List<string> { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" };
        var seen = new HashSet<string>(lines, StringComparer.Ordinal);

        foreach (var document in Documents)
        {
            foreach (var ch in Encoding.UTF8.GetString(document))
            {
                if (!char.IsWhiteSpace(ch))
                {
                    // Both the word-initial piece and its "##" continuation, so a number like 345
                    // tokenizes as 3 ##4 ##5 rather than [UNK].
                    var token = char.ToLowerInvariant(ch).ToString();
                    if (seen.Add(token))
                    {
                        lines.Add(token);
                        lines.Add("##" + token);
                    }
                }
            }
        }

        foreach (var word in Synthetic.Words)
        {
            if (seen.Add(word))
            {
                lines.Add(word);
            }
        }

        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }

    private static List<byte[]> Build()
    {
        var random = new Random(Synthetic.Seed);
        var documents = new List<byte[]>(DocumentCount);
        for (var d = 0; d < DocumentCount; d++)
        {
            documents.Add(Encoding.UTF8.GetBytes(Document(random, d)));
        }

        return documents;
    }

    private static string Document(Random random, int index)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(Title(random)).Append("\n\n");

        var sections = 3 + random.Next(3);
        for (var s = 0; s < sections; s++)
        {
            text.Append("## ").Append(Title(random)).Append("\n\n");

            var paragraphs = 2 + random.Next(2);
            for (var p = 0; p < paragraphs; p++)
            {
                text.Append(Paragraph(random)).Append("\n\n");
            }

            if (s == 1)
            {
                for (var i = 0; i < 4; i++)
                {
                    text.Append("- ").Append(Synthetic.Sentence(random, 6 + random.Next(6))).Append('\n');
                }

                text.Append('\n');
            }

            if (s == 2 && index % 2 == 0)
            {
                text.Append("| Name | Value | Note |\n|---|---|---|\n");
                for (var r = 0; r < 6; r++)
                {
                    text.Append("| ").Append(Synthetic.Words[random.Next(Synthetic.Words.Count)])
                        .Append(" | ").Append(random.Next(1000).ToString(CultureInfo.InvariantCulture))
                        .Append(" | ").Append(Synthetic.Sentence(random, 4)).Append(" |\n");
                }

                text.Append('\n');
            }
            else if (s == 2)
            {
                text.Append("```text\n");
                for (var l = 0; l < 5; l++)
                {
                    text.Append(Synthetic.Sentence(random, 5)).Append('\n');
                }

                text.Append("```\n\n");
            }
        }

        return text.ToString();
    }

    private static string Title(Random random) => Synthetic.Sentence(random, 2 + random.Next(3));

    private static string Paragraph(Random random)
    {
        var text = new StringBuilder();
        var sentences = 3 + random.Next(4);
        for (var i = 0; i < sentences; i++)
        {
            if (i != 0)
            {
                text.Append(' ');
            }

            text.Append(Synthetic.Sentence(random, 8 + random.Next(10))).Append('.');
        }

        return text.ToString();
    }
}
