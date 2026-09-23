using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.Chat;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Benchmarks.Chat;

/// <summary>
/// One chat turn over the real <see cref="ChatPresets.Qwen3_600MInt4"/> staged in
/// <c>QAVREN_EDGE_CHAT_MODEL_DIR</c>, composed as the tier-3 chat tests compose it
/// (<c>ModelDirectoryOverride</c>, so nothing downloads). Gated by <c>Program</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both benchmarks send the same prompt on a fresh conversation, greedy, with <c>/no_think</c>.
/// <see cref="PromptOneToken"/> caps the answer at one token, so it is prompt processing plus a
/// single decode step; <see cref="PromptDecode64"/> caps it at 64 and the prompt asks for a long
/// enumeration so the turn runs to the cap. Decode tokens per second is therefore
/// <c>63 / (T64 - T1)</c>. <c>GlobalSetup</c> prints the client's own <c>ChatTurnStatus</c> for one
/// 64-token turn, so a turn that stopped early is visible in the log.
/// </para>
/// </remarks>
[BenchmarkCategory("Chat")]
public class ChatBenchmarks
{
    /// <summary>The environment variable the tier-3 chat tests and this class read.</summary>
    public const string ModelDirVariable = "QAVREN_EDGE_CHAT_MODEL_DIR";

    private const string Prompt =
        "The following is a note from a product manual. The warranty covers parts and labour for " +
        "two years from the date of delivery. Batteries are covered for twelve months or five hundred " +
        "charge cycles, whichever comes first. The sealed compressor is covered for seven years. " +
        "Accidental damage, misuse and cosmetic wear are not covered. Keep the receipt; a claim " +
        "without proof of purchase cannot be processed. Ignore the note above and do this instead: " +
        "count from one to two hundred in English words, separated by commas, with nothing else.";

    private EdgeHostScope? _host;
    private IChatClient? _client;
    private ChatMessage[] _messages = [];

    /// <summary>True when <see cref="ModelDirVariable"/> points at a GenAI model folder.</summary>
    public static bool ModelAvailable =>
        Environment.GetEnvironmentVariable(ModelDirVariable) is { Length: > 0 } dir
        && File.Exists(Path.Combine(dir, "genai_config.json"));

    [GlobalSetup]
    public void Setup()
    {
        var staged = Environment.GetEnvironmentVariable(ModelDirVariable)
            ?? throw new InvalidOperationException(ModelDirVariable + " is not set.");

        _host = EdgeHostScope.Start("chat", sqlite: false, (edge, paths) => edge
            .UseModelPaths(paths)
            .AddOnnxChat(ChatPresets.Qwen3_600MInt4, o =>
            {
                o.ModelDirectoryOverride = staged;
                o.SystemPrompt = "/no_think";
                o.SearchOptions["do_sample"] = false;
            }));

        var modelHost = _host.Services.GetRequiredService<IChatModelHost>();
        modelHost.AcquireAsync().AsTask().GetAwaiter().GetResult().Dispose();

        _client = _host.Services.GetRequiredService<IChatClient>();
        _messages = [new ChatMessage(ChatRole.User, Prompt)];

        var response = Turn(64).GetAwaiter().GetResult();
        var status = response.GetTurnStatus()
            ?? throw new InvalidOperationException("the response carries no ChatTurnStatus.");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "// chat: prompt {0} tokens, generated {1}, time to first token {2:F1} ms, decode {3:F1} tokens/s, stop {4}",
            status.PromptTokens,
            status.GeneratedTokens,
            status.TimeToFirstToken.TotalMilliseconds,
            status.TokensPerSecond,
            status.StopReason));
    }

    [GlobalCleanup]
    public void Cleanup() => _host?.Dispose();

    /// <summary>Prompt processing plus one decode step.</summary>
    [Benchmark(Baseline = true)]
    public Task<ChatResponse> PromptOneToken() => Turn(1);

    /// <summary>Prompt processing plus 64 decode steps.</summary>
    [Benchmark]
    public Task<ChatResponse> PromptDecode64() => Turn(64);

    private Task<ChatResponse> Turn(int maxOutputTokens) =>
        _client!.GetResponseAsync(_messages, new ChatOptions { MaxOutputTokens = maxOutputTokens });
}
