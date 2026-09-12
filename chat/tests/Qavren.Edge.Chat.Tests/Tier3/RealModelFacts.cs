using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Chat.Tests.Tier2;
using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;
using Qavren.Edge.Rag;
using Xunit;
using GenAiModel = Microsoft.ML.OnnxRuntimeGenAI.Model;

namespace Qavren.Edge.Chat.Tests.Tier3;

/// <summary>One committed corpus chunk.</summary>
/// <param name="Id">The stable id a citation resolves to.</param>
/// <param name="Title">The block's <c>Title:</c> line.</param>
/// <param name="Text">Two to four sentences of a fictional appliance warranty.</param>
public sealed record CorpusChunk(string Id, string Title, string Text);

/// <summary>Source-generated, because this repo's only JSON path is source-generated (AOT-safe).</summary>
[JsonSerializable(typeof(List<CorpusChunk>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CorpusJsonContext : JsonSerializerContext;

/// <summary>
/// Tier 3. The real <c>Qwen3_600MInt4</c>, on a nightly schedule and never on a PR. Every fact is
/// gated on <see cref="ChatModelAvailable.Yes"/>, evaluated at runtime - so the container and the
/// 495 MB model are built inside the test body and a skipped run costs a lane nothing.
/// </summary>
/// <remarks>
/// <para>
/// The constants below are the ones <c>CorpusInvariantTests</c> reads <b>by reference</b>: the
/// question, the gold chunk, the gold sentence, the required-token rule and the corpus resource.
/// That is what stops the exclusivity invariant being checked against one corpus while the
/// nightly answers over another (plan adjustment 26).
/// </para>
/// <para>
/// Measurements - load time, tokens/sec, TTFT, peak RSS beside the arithmetic - are recorded
/// through <see cref="JobSummary"/> as artifacts and printed into the job summary, never asserted
/// tight: there is no measured device baseline to compare a hosted-runner number against.
/// </para>
/// </remarks>
[Collection(RealModelCollectionDefinition.Name)]
public sealed partial class RealModelFacts
{
    // No constructor. Nothing is loaded until a test body runs - see ChatModelAvailable's remarks.

    /// <summary>The one question the deterministic RAG assertion asks.</summary>
    public const string RagQuestion = "How many years does the warranty cover the compressor?";

    /// <summary>The chunk that answers it, and the only one carrying the required token.</summary>
    public const string GoldChunkId = "warranty-compressor";

    /// <summary>The sentence the gold chunk must carry verbatim.</summary>
    public const string GoldSentence = "the sealed compressor is covered for seven years from the date of purchase";

    /// <summary>The embedded resource the corpus is read from - every lane, the same way.</summary>
    public const string CorpusResourceName = "corpus.json";

    /// <summary>Plan adjustment 26's decoding: a fixed seed under greedy decoding.</summary>
    public const long RagSeed = 20260911;

    /// <summary>
    /// Qwen3's soft switch. Without it the model spends the output budget on a
    /// <c>&lt;think&gt;</c> block before it answers; the switch is documented for user prompts and
    /// system messages, and it sits in the system prompt so <see cref="RagQuestion"/> stays the
    /// literal the invariant guards.
    /// </summary>
    private const string NoThinkSystemPrompt = "Answer briefly. /no_think";

    private static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(120);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    [Fact(
        Skip = ChatModelAvailable.SkipReason,
        SkipUnless = nameof(ChatModelAvailable.Yes),
        SkipType = typeof(ChatModelAvailable))]
    public async Task TheModelLoadsInsideTheTimeBudgetRendersItsOwnTemplateAndAnswersCoherently()
    {
        await using var host = Tier3ChatHost.Build(o => o.SystemPrompt = NoThinkSystemPrompt);

        var info = await host.LoadAsync(Token).ConfigureAwait(true);

        Assert.True(info.IsLoaded);
        Assert.True(info.LoadDuration < LoadBudget, $"load took {info.LoadDuration}");

        // The single biggest per-preset unknown: minja parsed this model's Jinja.
        Assert.True(info.Backend.ChatTemplateSupported);
        Assert.Equal(ChatTemplateProbe.ModelTemplateFormatter, info.Backend.PromptFormatter);

        JobSummary.Record("tier3-load", "presetId", info.PresetId);
        JobSummary.Record("tier3-load", "loadMs", info.LoadDuration.TotalMilliseconds);
        JobSummary.Record("tier3-load", "providers", string.Join(", ", info.Backend.Providers));
        JobSummary.Record("tier3-load", "chatTemplateSupported", info.Backend.ChatTemplateSupported);
        JobSummary.Record("tier3-load", "resolvedContextTokens", info.ResolvedContextTokens);
        JobSummary.Record("tier3-load", "budgetVerdict", info.Budget.Verdict);

        var response = await host.Client.GetResponseAsync(
            [User("In one short sentence, what colour is a clear daytime sky?")],
            new ChatOptions { MaxOutputTokens = 64 },
            Token).ConfigureAwait(true);

        var answer = StripThinking(response.Text);
        var status = response.GetTurnStatus();

        Assert.NotNull(status);
        Assert.True(status.GeneratedTokens > 0);
        Assert.False(string.IsNullOrWhiteSpace(answer), "the model produced no text");
        Assert.Contains(answer, char.IsLetter);
        Assert.DoesNotContain('�', answer);

        // Artifacts, not assertions.
        JobSummary.Record("tier3-turn", "generatedTokens", status.GeneratedTokens);
        JobSummary.Record("tier3-turn", "promptTokens", status.PromptTokens);
        JobSummary.Record("tier3-turn", "tokensPerSecond", status.TokensPerSecond);
        JobSummary.Record("tier3-turn", "ttftMs", status.TimeToFirstToken.TotalMilliseconds);
        JobSummary.Record("tier3-turn", "stopReason", status.StopReason);
        JobSummary.Record("tier3-turn", "answer", answer);
    }

    [Fact(
        Skip = ChatModelAvailable.SkipReason,
        SkipUnless = nameof(ChatModelAvailable.Yes),
        SkipType = typeof(ChatModelAvailable))]
    public async Task TheResolvedMaxLengthMatchesTheBudgetsDecisionAndPeakRssIsRecordedBesideTheArithmetic()
    {
        await using var host = Tier3ChatHost.Build(o => o.SystemPrompt = NoThinkSystemPrompt);
        var info = await host.LoadAsync(Token).ConfigureAwait(true);

        // One turn, so the conversation cache holds the lease and - both shipped presets set
        // past_present_share_buffer, the static KV path - the cache is allocated to max_length.
        await host.Client.GetResponseAsync([User("Say hello.")], new ChatOptions { MaxOutputTokens = 8 }, Token)
            .ConfigureAwait(true);

        var model = host.Client.GetService<GenAiModel>();
        Assert.NotNull(model);

        using (var parameters = new GeneratorParams(model))
        {
            // The config the model was built from carries the budget's answer, read back through
            // the public GeneratorParams surface.
            Assert.Equal(info.ResolvedContextTokens, parameters.GetSearchNumber("max_length"));
        }

        JobSummary.Record("tier3-budget", "budgetVerdict", info.Budget.Verdict);
        JobSummary.Record("tier3-budget", "budgetExplanation", info.Budget.Explanation);
        JobSummary.Record("tier3-budget", "resolvedContextTokens", info.ResolvedContextTokens);
        JobSummary.Record("tier3-budget", "availableBytes", info.Budget.AvailableBytes);
        JobSummary.Record("tier3-budget", "usableBytes", info.Budget.UsableBytes);

        // Spec 19 item 4: peak RSS at the resolved context beside the arithmetic, so the first
        // nightly answers whether KvCacheBytesPerElement is 2 or 4 for this build.
        using var process = Process.GetCurrentProcess();
        var peak = process.PeakWorkingSet64;
        var kvAt2 = info.Shape.KvCacheBytes(info.ResolvedContextTokens);
        var required = ChatMemoryBudget.RequiredBytes(info.Shape, info.ResolvedContextTokens, new ChatMemoryBudgetOptions());
        var overWeights = peak - info.Shape.WeightsBytes;

        JobSummary.Record("tier3-rss", "peakWorkingSetBytes", peak);
        JobSummary.Record("tier3-rss", "weightsBytes", info.Shape.WeightsBytes);
        JobSummary.Record("tier3-rss", "kvCacheBytesAt2PerElement", kvAt2);
        JobSummary.Record("tier3-rss", "kvCacheBytesAt4PerElement", kvAt2 * 2);
        JobSummary.Record("tier3-rss", "requiredBytesAt2PerElement", required);
        JobSummary.Record("tier3-rss", "peakMinusWeightsBytes", overWeights);
        JobSummary.Record(
            "tier3-rss",
            "peakMinusWeightsIsCloserTo",
            Math.Abs(overWeights - kvAt2) <= Math.Abs(overWeights - (kvAt2 * 2)) ? "2 bytes/element" : "4 bytes/element");

        Assert.True(peak > 0);
    }

    [Fact(
        Skip = ChatModelAvailable.SkipReason,
        SkipUnless = nameof(ChatModelAvailable.Yes),
        SkipType = typeof(ChatModelAvailable))]
    public async Task DeterministicRagQualityOverTheCommittedCorpus()
    {
        // Zero new package dependencies: an in-memory DelegateRetriever over the committed
        // corpus.json, so this measures the answer and not sub-project 2.
        var retriever = new DelegateRetriever("committed-corpus", RetrieveAsync);

        await using var host = Tier3ChatHost.Build(
            o =>
            {
                o.SystemPrompt = NoThinkSystemPrompt;
                o.SearchOptions["do_sample"] = false;
            },
            pipeline: builder => builder.UseRag(retriever, rag =>
            {
                rag.Top = 5;
                rag.MaxCharsPerSource = 1200;
            }));

        await host.LoadAsync(Token).ConfigureAwait(true);

        var response = await host.Client.GetResponseAsync(
            [User(RagQuestion)],
            new ChatOptions { MaxOutputTokens = 96, Seed = RagSeed },
            Token).ConfigureAwait(true);

        var sources = FindSources(response);
        var answer = response.Text;

        JobSummary.Record("tier3-rag", "retrievedIds", string.Join(", ", sources.Select(s => $"{s.Ordinal}:{s.Id}")));
        JobSummary.Record("tier3-rag", "answer", answer);
        JobSummary.Record("tier3-rag", "generatedTokens", response.GetTurnStatus()?.GeneratedTokens);

        // Three assertions on the answer, all hard.

        // 1. Every [n] marker is within the retrieved-source count and resolves to one of them.
        foreach (var marker in MarkerRegex().Matches(answer).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
        {
            Assert.InRange(marker, 1, sources.Count);
            Assert.Contains(sources, s => s.Ordinal == marker);
        }

        // 2. The gold chunk is among the five retrieved.
        Assert.True(sources.Count <= 5);
        Assert.Contains(sources, s => s.Id == GoldChunkId);

        // 3. The answer contains the required token - which, by CorpusInvariantTests, no chunk but
        //    the gold one could have supplied. If this proves flaky across the first week of
        //    nightlies, the documented response is to demote it to a recorded metric and say so in
        //    chat/README.md - never to delete it quietly.
        Assert.True(ContainsRequiredToken(answer), $"the answer does not contain 'seven' or a bare 7: {answer}");
    }

    [Fact(
        Skip = ChatModelAvailable.SkipReason,
        SkipUnless = nameof(ChatModelAvailable.Yes),
        SkipType = typeof(ChatModelAvailable))]
    public async Task TheConversationCacheSeamIsLosslessForThisPreset()
    {
        // Spec 19 item 11, per preset on the nightly: the append path encodes only the delta, so
        // a BPE merge spanning the join would be lost. Encode the pieces, encode the whole, compare.
        await using var host = Tier3ChatHost.Build();
        await host.LoadAsync(Token).ConfigureAwait(true);

        const string q1 = "Name one primary colour.";
        const string q2 = "And another?";

        var first = await host.Client.GetResponseAsync([User(q1)], new ChatOptions { MaxOutputTokens = 24 }, Token).ConfigureAwait(true);
        var id = first.ConversationId;
        Assert.NotNull(id);

        var second = await host.Client.GetResponseAsync(
            [User(q1), Assistant(first.Text), User(q2)],
            new ChatOptions { MaxOutputTokens = 8, ConversationId = id },
            Token).ConfigureAwait(true);

        var status = second.GetTurnStatus()!;
        var hit = status.PromptTokensAppended < status.PromptTokens;

        var tokenizer = host.Client.GetService<Tokenizer>();
        Assert.NotNull(tokenizer);

        var firstPrompt = Render(tokenizer, [User(q1)]);
        var secondPrompt = Render(tokenizer, [User(q1), Assistant(first.Text), User(q2)]);
        var cachedText = firstPrompt + first.Text;
        var prefixHolds = secondPrompt.StartsWith(cachedText, StringComparison.Ordinal);

        JobSummary.Record("tier3-cache-seam", "prefixHolds", prefixHolds);
        JobSummary.Record("tier3-cache-seam", "cacheHit", hit);

        if (!prefixHolds)
        {
            // The template renders assistant history differently from the decoded text (Qwen3's
            // template strips reasoning from earlier turns, for one), so the client rebuilds every
            // follow-up for this preset. Recorded; the one-line fallback is
            // EnableConversationCache = false in the preset's own definition.
            Assert.False(hit, "the prefix check failed but the client reported a cache hit");
            JobSummary.Record("tier3-cache-seam", "seamLossless", "(not reached: the prefix check rebuilds)");
            return;
        }

        var delta = secondPrompt[cachedText.Length..];
        int[] piecewise = [.. Encode(tokenizer, firstPrompt), .. Encode(tokenizer, first.Text), .. Encode(tokenizer, delta)];
        var whole = Encode(tokenizer, secondPrompt);

        JobSummary.Record("tier3-cache-seam", "deltaTokens", Encode(tokenizer, delta).Length);
        JobSummary.Record("tier3-cache-seam", "wholeTokens", whole.Length);
        JobSummary.Record("tier3-cache-seam", "seamLossless", piecewise.AsSpan().SequenceEqual(whole));

        Assert.Equal(whole, piecewise);
    }

    /// <summary>The committed corpus, from the embedded resource every lane carries.</summary>
    /// <returns>The twenty chunks, in file order.</returns>
    public static IReadOnlyList<CorpusChunk> LoadCorpus()
    {
        using var stream = typeof(RealModelFacts).Assembly.GetManifestResourceStream(CorpusResourceName)
            ?? throw new InvalidOperationException($"'{CorpusResourceName}' is not an embedded resource of this assembly.");

        return JsonSerializer.Deserialize(stream, CorpusJsonContext.Default.ListCorpusChunk)
            ?? throw new InvalidOperationException($"'{CorpusResourceName}' deserialised to null.");
    }

    /// <summary>Plan adjustment 26's required-token rule: <c>seven</c> case-insensitively, or a bare <c>7</c>.</summary>
    /// <param name="answer">The model's answer.</param>
    /// <returns>Whether the token is present.</returns>
    public static bool ContainsRequiredToken(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return RequiredTokenRegex().IsMatch(answer);
    }

    /// <summary>
    /// A deterministic keyword retriever over the corpus: the score is how many distinct query
    /// terms (three-plus letters, a small stop list) appear as whole words in the chunk's title and
    /// text; ties keep file order. For <see cref="RagQuestion"/> the gold chunk scores 3 (years,
    /// cover, compressor) and every other chunk at most 1, so it is always retrieved first.
    /// </summary>
    internal static Task<IEnumerable<RagSource>> RetrieveAsync(string query, RetrievalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var terms = Words(query).Where(w => w.Length >= 3 && !StopWords.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = LoadCorpus()
            .Select((chunk, index) =>
            {
                var words = Words(chunk.Title + " " + chunk.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (chunk, index, score: terms.Count(words.Contains));
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(request.Top)
            .Select(x => new RagSource(x.chunk.Id, x.chunk.Text)
            {
                Title = x.chunk.Title,
                Score = x.score,
                ScoreKind = RetrievalScoreKind.Relevance,
            })
            .ToList();

        return Task.FromResult<IEnumerable<RagSource>>(ranked);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "does", "how", "many", "with", "that", "this", "from", "not",
        "its", "was", "were", "has", "have", "had", "can", "you", "your", "our", "what", "which",
    };

    private static IEnumerable<string> Words(string text) =>
        WordRegex().Matches(text).Select(m => m.Value);

    private static string Render(Tokenizer tokenizer, IReadOnlyList<ChatMessage> messages) =>
        tokenizer.ApplyChatTemplate(null!, ChatTurnPipeline.MessagesToJson(messages), null!, true);

    private static int[] Encode(Tokenizer tokenizer, string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        using var sequences = tokenizer.Encode(text);
        return sequences[0].ToArray();
    }

    private static string StripThinking(string text) => ThinkRegex().Replace(text, string.Empty).Trim();

    private static IReadOnlyList<RagSource> FindSources(ChatResponse response)
    {
        if (response.AdditionalProperties?.TryGetValue(RagCitations.SourcesPropertyKey, out var onResponse) == true
            && onResponse is IReadOnlyList<RagSource> fromResponse)
        {
            return fromResponse;
        }

        foreach (var message in response.Messages)
        {
            if (message.AdditionalProperties?.TryGetValue(RagCitations.SourcesPropertyKey, out var onMessage) == true
                && onMessage is IReadOnlyList<RagSource> fromMessage)
            {
                return fromMessage;
            }
        }

        Assert.Fail("the response carries no " + RagCitations.SourcesPropertyKey);
        return [];
    }

    [GeneratedRegex(@"seven|\b7\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequiredTokenRegex();

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex("[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ThinkRegex();
}

/// <summary>
/// A container over the real model staged in <c>QAVREN_EDGE_CHAT_MODEL_DIR</c>. Built inside a
/// test body, never in a constructor - see <see cref="ChatModelAvailable"/>.
/// </summary>
/// <remarks>
/// <b>Nothing here can download.</b> <c>EdgeChatOptions.ModelDirectoryOverride</c> points the
/// provisioner at the staged directory, which the workflow has already fetched and hash-verified,
/// and <c>IsProvisioned</c> is then a directory-exists check. The staged directory is never
/// written to; the model root is a fresh scratch directory, deleted on dispose.
/// </remarks>
internal sealed class Tier3ChatHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _root;

    private Tier3ChatHost(ServiceProvider services, string root)
    {
        _services = services;
        _root = root;
    }

    /// <summary>The container.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The pipeline's outermost client - the RAG middleware when one was asked for.</summary>
    public IChatClient Client => _services.GetRequiredService<IChatClient>();

    /// <summary>The registration's host.</summary>
    public IChatModelHost Host => _services.GetRequiredService<IChatModelHost>();

    /// <summary>Builds the container. Lazy: no model is loaded until <see cref="LoadAsync"/> or the first turn.</summary>
    /// <param name="configure">Configures the registration's options.</param>
    /// <param name="pipeline">The MEAI pipeline over the leaf client.</param>
    /// <returns>The host.</returns>
    public static Tier3ChatHost Build(Action<EdgeChatOptions>? configure = null, Action<ChatClientBuilder>? pipeline = null)
    {
        var staged = ChatModelAvailable.StagedDirectory;
        var root = Path.Combine(Path.GetTempPath(), "qedge-chat-tier3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Information));

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first.
        services.AddSingleton<IEdgePaths>(new TempEdgePaths(root));

        services.AddQavrenEdge(edge => edge
            .UseModelPaths(new FakeModelPaths(root))
            .AddOnnxChat(
                ChatPresets.Qwen3_600MInt4,
                options =>
                {
                    options.ModelDirectoryOverride = staged;
                    configure?.Invoke(options);
                },
                pipeline));

        return new Tier3ChatHost(services.BuildServiceProvider(), root);
    }

    /// <summary>Starts the host and loads the model through the ordinary load path.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the host knows once the model is resident.</returns>
    public async ValueTask<ChatModelInfo> LoadAsync(CancellationToken cancellationToken)
    {
        await _services.GetRequiredService<IEdgeHost>().EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var lease = await Host.AcquireAsync(cancellationToken).ConfigureAwait(false);
        lease.Dispose();

        return Host.Describe() ?? throw new InvalidOperationException("The host loaded nothing.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked cache file must not fail a green nightly.
        }
    }
}
