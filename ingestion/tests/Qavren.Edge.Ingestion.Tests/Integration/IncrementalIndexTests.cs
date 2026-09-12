using System.Globalization;
using System.Text.RegularExpressions;
using CsCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 14.3's incremental claims, each one a mechanism rather than a golden: the zero-embed
/// re-run, the one-paragraph edit, the head insert with its byte-identical vectors, the recipe
/// bump, the unselected-extractor bump, and a model-based property over random single-character
/// edits. Task 7.1 Step 2.
/// </summary>
public sealed partial class IncrementalIndexTests
{
    /// <summary>
    /// Twenty paragraphs of 150 whitespace tokens each. One paragraph fits the 222-token budget;
    /// two do not; so the plain chunker emits exactly one chunk per paragraph.
    /// </summary>
    private const int Paragraphs = 20;
    private const int WordsPerParagraph = 150;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [GeneratedRegex("[0-9a-f]{32}")]
    private static partial Regex Hex32 { get; }

    [Fact]
    public async Task A_re_run_over_an_unchanged_corpus_embeds_nothing_and_skips_everything()
    {
        using var host = await IntegrationHost.StartAsync();
        var corpus = CorpusRoundTripTests.Corpus(CorpusRoundTripTests.TextCorpus);

        await host.Pipeline.RunAsync(corpus, cancellationToken: Token);
        var callsAfterFirst = host.Generator.CallCount;

        var second = await host.Pipeline.RunAsync(corpus, cancellationToken: Token);

        Assert.Equal(0, second.EmbedCalls);
        Assert.Equal(0, second.ChunksAdded);
        Assert.Equal(second.DocumentsSeen, second.DocumentsSkipped);
        Assert.Equal(callsAfterFirst, host.Generator.CallCount);
    }

    [Fact]
    public async Task Editing_one_paragraph_adds_exactly_one_chunk_and_removes_exactly_one()
    {
        using var host = await IntegrationHost.StartAsync();
        var source = new RecordingSource().Add("long.txt", Prose.Document(Paragraphs, WordsPerParagraph));

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(Paragraphs, first.ChunksAdded);

        // A same-length replacement in paragraph 10, so no offset after it moves and the ONLY
        // difference between the two runs is that one chunk's content hash changed.
        var edited = Prose.Document(Paragraphs, WordsPerParagraph)
            .Replace("p10w7 ", "p10X7 ", StringComparison.Ordinal);
        source.Replace("long.txt", edited);

        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, second.ChunksAdded);
        Assert.Equal(1, second.ChunksRemoved);
        Assert.Equal(Paragraphs - 1, second.ChunksUnchanged);
        Assert.Equal(0, second.ChunksRepaired);
        Assert.Equal(1, second.EmbedCalls);

        var input = Assert.Single(host.Generator.Calls[^1]);
        Assert.Contains("p10X7", input, StringComparison.Ordinal);
        Assert.Equal(Paragraphs, await Db.CountAsync(host.Database, Db.DataTable));
    }

    /// <summary>
    /// Spec 9.5's honest number: inserting a paragraph at the head of a 20-chunk document is one
    /// embed and nineteen repairs - and the nineteen vectors are not touched, which is what the
    /// vec0 rows read back byte-identical proves. The FTS5 assertions are the only guard on SP1's
    /// <c>_au</c> trigger path, which the repair's <c>UPDATE</c> fires per row.
    /// </summary>
    [Fact]
    public async Task Inserting_a_paragraph_at_the_head_is_one_embed_nineteen_repairs_and_no_vector_rewrite()
    {
        using var host = await IntegrationHost.StartAsync();
        var source = new RecordingSource().Add("long.txt", Prose.Document(Paragraphs, WordsPerParagraph));

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(Paragraphs, first.ChunksAdded);

        var before = await Db.VectorRowsAsync(host.Database);
        var keysBefore = await Db.KeysAsync(host.Database, "long.txt");
        Assert.Equal(Paragraphs, before.Count);

        // A new paragraph at the head: every existing paragraph's ordinal and offsets shift.
        source.Replace(
            "long.txt",
            Prose.Paragraph(99, WordsPerParagraph) + "\n\n" + Prose.Document(Paragraphs, WordsPerParagraph));

        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, second.ChunksAdded);
        Assert.Equal(0, second.ChunksRemoved);
        Assert.Equal(Paragraphs, second.ChunksRepaired);
        Assert.Equal(1, second.EmbedCalls);
        Assert.Contains(host.Logs.Lines, l => l.StartsWith("916|", StringComparison.Ordinal));

        // The nineteen (here: all twenty original) vec0 rows are byte-identical by direct SQL:
        // same rowid, same blob. The one new row is the only difference.
        var after = await Db.VectorRowsAsync(host.Database);
        Assert.Equal(Paragraphs + 1, after.Count);
        var afterByRowId = after.ToDictionary(r => r.RowId);
        foreach (var (rowId, vector) in before)
        {
            Assert.True(afterByRowId.TryGetValue(rowId, out var same), $"vec0 rowid {rowId} disappeared");
            Assert.Equal(vector, same.Vector);
        }

        // The keys survived and only their ordinals moved, by one.
        var chunks = await Db.ChunksAsync(host.Database, "long.txt");
        Assert.Equal(keysBefore, chunks.Skip(1).Select(c => c.Key).ToList());
        Assert.Equal(Enumerable.Range(0, Paragraphs + 1), chunks.Select(c => c.Ordinal));

        // The FTS5 sidecar is still correct after twenty delete-plus-insert trigger firings: the
        // external-content integrity check passes, and a word from the LAST paragraph still hits.
        await Db.FtsIntegrityCheckAsync(host.Database);
        Assert.Equal(1, await Db.FtsMatchCountAsync(host.Database, "p19w3"));
        Assert.Equal(1, await Db.FtsMatchCountAsync(host.Database, "p99w0"));
        Assert.Equal(Paragraphs + 1, await Db.CountAsync(host.Database, Db.FullTextTable));
    }

    /// <summary>
    /// A real recipe bump - <c>MaxTokens</c> changed between two hosts over one database - and
    /// every document is re-indexed, with event 915 exactly once carrying BOTH hashes.
    /// </summary>
    [Fact]
    public async Task Bumping_the_recipe_re_indexes_every_document_and_logs_915_once_with_both_hashes()
    {
        var root = IntegrationHost.NewRoot();
        var corpus = CorpusRoundTripTests.Corpus(CorpusRoundTripTests.TextCorpus);
        try
        {
            string firstRecipe;
            using (var first = await IntegrationHost.StartAsync(new IntegrationHostOptions { Root = root }))
            {
                var run = await first.Pipeline.RunAsync(corpus, cancellationToken: Token);
                Assert.Equal(IngestionRunOutcome.Completed, run.Outcome);
                firstRecipe = run.RecipeHash;
            }

            using var second = await IntegrationHost.StartAsync(new IntegrationHostOptions
            {
                Root = root,
                Ingestion = o => o.Chunking.MaxTokens = 200,
            });

            var result = await second.Pipeline.RunAsync(corpus, cancellationToken: Token);

            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
            Assert.NotEqual(firstRecipe, result.RecipeHash);

            // Every document is re-indexed: none skips the hash gate. Eleven come back Indexed and
            // empty.txt comes back NoTextLayer again - re-extracted, not skipped - which is the
            // outcome a document with no text has whatever the recipe.
            Assert.Equal(0, result.DocumentsSkipped);
            Assert.Equal(CorpusRoundTripTests.TextCorpus.Length - 1, result.DocumentsIndexed);
            Assert.Equal(
                CorpusRoundTripTests.TextCorpus.Length,
                result.Documents.Count(d => d.Outcome is IngestionDocumentOutcome.Indexed or IngestionDocumentOutcome.NoTextLayer));

            var drift = Assert.Single(second.Logs.Lines, l => l.StartsWith("915|", StringComparison.Ordinal));
            var hashes = Hex32.Matches(drift).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
            Assert.Equal(2, hashes.Count);
        }
        finally
        {
            IntegrationHost.DeleteRoot(root);
        }
    }

    /// <summary>
    /// The other half of spec 9.2: registering the real PDF satellite over a text corpus - an
    /// extractor nothing selects - re-indexes nothing. Task 5.1 proved it with a widget; this is
    /// the shipped satellite.
    /// </summary>
    [Fact]
    public async Task Registering_the_unselected_pdf_extractor_re_indexes_nothing()
    {
        var root = IntegrationHost.NewRoot();
        var corpus = CorpusRoundTripTests.Corpus(CorpusRoundTripTests.TextCorpus);
        try
        {
            using (var first = await IntegrationHost.StartAsync(new IntegrationHostOptions { Root = root }))
            {
                await first.Pipeline.RunAsync(corpus, cancellationToken: Token);
            }

            using var second = await IntegrationHost.StartAsync(new IntegrationHostOptions
            {
                Root = root,
                AfterIngestion = edge => edge.AddPdfExtractor(),
            });

            var result = await second.Pipeline.RunAsync(corpus, cancellationToken: Token);

            Assert.Equal(CorpusRoundTripTests.TextCorpus.Length, result.DocumentsSkipped);
            Assert.Equal(0, result.DocumentsIndexed);
            Assert.Equal(0, result.EmbedCalls);
            Assert.DoesNotContain(second.Logs.Lines, l => l.StartsWith("915|", StringComparison.Ordinal));
        }
        finally
        {
            IntegrationHost.DeleteRoot(root);
        }
    }

    /// <summary>
    /// The property behind every claim above. The MODEL is naive: after every edit it re-extracts,
    /// re-chunks and re-identifies every document from scratch and remembers the expected key set.
    /// The ACTUAL is the incremental pipeline. After each random single-character edit the two must
    /// agree on the stored keys per document - and, inside the operation, an unchanged document
    /// re-embeds ZERO chunks while the edited one re-embeds exactly the chunks whose hash moved.
    /// </summary>
    [Fact]
    public async Task Random_single_character_edits_re_embed_only_the_chunks_whose_hash_moved()
    {
        var edits = Gen.Select(Gen.Int[0, Session.DocumentCount - 1], Gen.Int[0, 5000], Gen.Char['a', 'z']);

        var operation = edits.Operation<Session, Model>(
            e => string.Format(CultureInfo.InvariantCulture, "edit doc {0} at {1} to '{2}'", e.Item1, e.Item2, e.Item3),
            async (session, e) => await session.EditAndRunAsync(e.Item1, e.Item2, e.Item3).ConfigureAwait(false),
            async (model, e) => await model.EditAsync(e.Item1, e.Item2, e.Item3).ConfigureAwait(false));

        await Check.SampleModelBasedAsync(
            Gen.Const(0).Select(_ => Session.StartAsync()),
            operation,
            equal: (session, model) => session.Stored.Count == model.Expected.Count
                && session.Stored.All(kv => model.Expected.TryGetValue(kv.Key, out var keys)
                    && kv.Value.SequenceEqual(keys, StringComparer.Ordinal)),
            iter: 12,
            threads: 1,
            printActual: s => Print(s.Stored),
            printModel: m => Print(m.Expected));
    }

    private static string Print(IReadOnlyDictionary<string, IReadOnlyList<string>> keys) =>
        string.Join("; ", keys.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=[" + string.Join(",", kv.Value.Select(k => k[..8])) + "]"));

    /// <summary>The corpus both sides start from: three documents, six short paragraphs each.</summary>
    private static string[] InitialTexts() =>
        [.. Enumerable.Range(0, Session.DocumentCount).Select(d => Prose.Document(6, 60, firstIndex: d * 10))];

    private static string Edit(string text, int position, char replacement)
    {
        if (text.Length == 0)
        {
            return replacement.ToString();
        }

        var at = position % text.Length;
        return string.Create(text.Length, (text, at, replacement), static (span, state) =>
        {
            state.text.AsSpan().CopyTo(span);
            span[state.at] = state.replacement;
        });
    }

    private static string DocumentId(int index) =>
        string.Format(CultureInfo.InvariantCulture, "doc{0}.txt", index);

    /// <summary>The actual: one host, one source, and the stored keys read back after every run.</summary>
    private sealed class Session : IDisposable
    {
        public const int DocumentCount = 3;

        private readonly IntegrationHost _host;
        private readonly RecordingSource _source;
        private readonly string[] _texts;
        private readonly Dictionary<string, IReadOnlyList<string>> _stored = new(StringComparer.Ordinal);

        private Session(IntegrationHost host, RecordingSource source, string[] texts)
        {
            _host = host;
            _source = source;
            _texts = texts;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> Stored => _stored;

        public static async Task<(Session, Model)> StartAsync()
        {
            var host = await IntegrationHost.StartAsync().ConfigureAwait(false);
            var texts = InitialTexts();
            var source = new RecordingSource("memory");
            for (var i = 0; i < DocumentCount; i++)
            {
                source.Add(DocumentId(i), texts[i]);
            }

            var session = new Session(host, source, texts);
            var first = await host.Pipeline.RunAsync(source, cancellationToken: Token).ConfigureAwait(false);
            Assert.Equal(IngestionRunOutcome.Completed, first.Outcome);
            await session.ReadStoredAsync().ConfigureAwait(false);

            var model = await Model.StartAsync(texts).ConfigureAwait(false);
            return (session, model);
        }

        public async Task EditAndRunAsync(int document, int position, char replacement)
        {
            var id = DocumentId(document);
            var before = _stored[id];
            _texts[document] = Edit(_texts[document], position, replacement);
            _source.Replace(id, _texts[document]);

            var callsBefore = _host.Generator.CallCount;
            var result = await _host.Pipeline.RunAsync(_source, cancellationToken: Token).ConfigureAwait(false);
            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);

            // Unchanged documents re-embed zero chunks: they never leave the hash gate.
            foreach (var other in result.Documents.Where(d => !string.Equals(d.DocumentId, id, StringComparison.Ordinal)))
            {
                Assert.Equal(IngestionDocumentOutcome.Skipped, other.Outcome);
                Assert.Equal(0, other.ChunksAdded);
            }

            await ReadStoredAsync().ConfigureAwait(false);
            var after = _stored[id];

            // The edited document re-embeds exactly the chunks whose hash moved - the keys that
            // are stored now and were not before - and nothing else reached the generator.
            var added = after.Except(before, StringComparer.Ordinal).Count();
            var removed = before.Except(after, StringComparer.Ordinal).Count();
            var edited = Assert.Single(result.Documents, d => string.Equals(d.DocumentId, id, StringComparison.Ordinal));
            Assert.Equal(added, edited.ChunksAdded);
            Assert.Equal(removed, edited.ChunksRemoved);

            var embedded = _host.Generator.Calls.Skip(callsBefore).Sum(call => call.Count);
            Assert.Equal(added, embedded);
        }

        private async Task ReadStoredAsync()
        {
            for (var i = 0; i < DocumentCount; i++)
            {
                _stored[DocumentId(i)] = await Db.KeysAsync(_host.Database, DocumentId(i)).ConfigureAwait(false);
            }
        }

        public void Dispose() => _host.Dispose();
    }

    /// <summary>The model: naive, stateless re-computation of every document's keys after every edit.</summary>
    private sealed class Model
    {
        private static readonly ResolvedChunkOptions Budget =
            new ChunkOptions().Resolve(ChunkModelProfile.MiniLmL6V2Int8, new FakeChunkTokenizer());

        private readonly string[] _texts;
        private readonly Dictionary<string, IReadOnlyList<string>> _expected = new(StringComparer.Ordinal);

        private Model(string[] texts) => _texts = texts;

        public IReadOnlyDictionary<string, IReadOnlyList<string>> Expected => _expected;

        public static async Task<Model> StartAsync(string[] texts)
        {
            var model = new Model([.. texts]);
            for (var i = 0; i < texts.Length; i++)
            {
                await model.RecomputeAsync(i).ConfigureAwait(false);
            }

            return model;
        }

        public async Task EditAsync(int document, int position, char replacement)
        {
            _texts[document] = Edit(_texts[document], position, replacement);
            await RecomputeAsync(document).ConfigureAwait(false);
        }

        private async Task RecomputeAsync(int document)
        {
            var id = DocumentId(document);
            var bytes = System.Text.Encoding.UTF8.GetBytes(_texts[document]);
            var item = new DocumentSourceItem(
                id,
                IngestionMediaTypes.PlainText,
                _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
            {
                SizeBytes = bytes.Length,
            };

            var extracted = await new PlainTextExtractor()
                .ExtractAsync(item, TestHarness.Context(new CapturingLogger()), Token)
                .ConfigureAwait(false);

            var chunker = ChunkerFactory.Resolve(ChunkerIds.Auto, IngestionMediaTypes.PlainText, NullLogger.Instance);
            var drafts = chunker.Chunk(extracted, Budget, new FakeChunkTokenizer()).ToList();
            _expected[id] = [.. ChunkDiff.Identify("memory", id, drafts).Select(c => c.Key)];
        }
    }
}
