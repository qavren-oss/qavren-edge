using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// <c>IngestionStage.Extracting</c> and <c>Chunking</c> used to be vocabulary nothing reported:
/// the snapshot went straight from the document boundary to Embedding. Both are read here from
/// INSIDE the call they name, which is the only place the stage can be observed from outside.
/// </summary>
public sealed class ProgressStageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_stage_is_Extracting_while_an_extractor_runs()
    {
        IngestionTestHost? host = null;
        var seen = new List<(IngestionStage Stage, string? Document)>();
        var probe = new StageProbeExtractor(() =>
        {
            if (host!.Pipeline.Current is { } current)
            {
                seen.Add((current.Stage, current.CurrentDocumentId));
            }
        });

        host = await IngestionTestHost.StartAsync(o => o.Extractors.Add(probe));
        using (host)
        {
            var source = new RecordingSource().Add("a.txt", "alpha beta gamma");

            await host.Pipeline.RunAsync(source, cancellationToken: Token);

            var observed = Assert.Single(seen);
            Assert.Equal(IngestionStage.Extracting, observed.Stage);
            Assert.Equal("a.txt", observed.Document);
        }
    }

    [Fact]
    public async Task The_stage_is_Chunking_while_the_chunker_counts_tokens()
    {
        IngestionTestHost? host = null;
        var stages = new HashSet<IngestionStage>();
        var tokenizer = new StageProbeTokenizer(() =>
        {
            if (host?.Pipeline.Current is { } current)
            {
                stages.Add(current.Stage);
            }
        });

        host = await IngestionTestHost.StartAsync(
            configureBeforeIngestion: edge => edge.Services.AddSingleton<IChunkTokenizer>(tokenizer));
        using (host)
        {
            var source = new RecordingSource().Add("a.txt", "alpha beta gamma");

            await host.Pipeline.RunAsync(source, cancellationToken: Token);

            // The embed step counts tokens too, so Embedding is seen as well; what matters is that
            // the chunker's own calls were made under Chunking.
            Assert.Contains(IngestionStage.Chunking, stages);
        }
    }

    private sealed class StageProbeExtractor(Action probe) : IDocumentExtractor
    {
        private readonly PlainTextExtractor _inner = new();

        public string Id => "stage-probe";

        public int Version => 1;

        public IReadOnlyList<string> Extensions => _inner.Extensions;

        public IReadOnlyList<string> MediaTypes => _inner.MediaTypes;

        public bool CanExtract(DocumentSourceItem item) => true;

        public ValueTask<ExtractedDocument> ExtractAsync(
            DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken)
        {
            probe();
            return _inner.ExtractAsync(item, context, cancellationToken);
        }
    }

    private sealed class StageProbeTokenizer(Action probe) : IChunkTokenizer
    {
        private readonly FakeChunkTokenizer _inner = new();

        public string Id => _inner.Id;

        public int MaxSequenceLength => _inner.MaxSequenceLength;

        public int SpecialTokenOverhead => _inner.SpecialTokenOverhead;

        public int CountTokens(ReadOnlySpan<char> text)
        {
            probe();
            return _inner.CountTokens(text);
        }

        public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
        {
            probe();
            return _inner.IndexByTokenCount(text, maxTokens, out tokenCount);
        }
    }
}
