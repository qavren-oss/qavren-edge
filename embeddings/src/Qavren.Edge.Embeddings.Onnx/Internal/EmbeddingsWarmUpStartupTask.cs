using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Onnx.Internal;

/// <summary>
/// Startup order 220. Parses the vocabulary so the first user-visible call does not.
/// </summary>
/// <remarks>
/// <see cref="IServiceProvider"/> rather than the provider itself for the same reason every other
/// SP2 startup task does it: <c>EdgeHost</c>'s constructor eagerly resolves every startup task, and
/// resolving the generator here would drag <c>IEdgeHost</c> into its own construction path.
/// <para>
/// This task is the TOKENIZER half only, and it is the ONLY task
/// <c>WarmUpEmbeddingsAtStartup</c> registers. Spec 7 presents the session half,
/// <c>Qavren.Edge.Onnx</c>'s <c>WarmUpSessionAtStartup</c>, as an independent opt-in, so an app
/// that wants the graph loaded at startup too calls that method itself with the preset's model id.
/// </para>
/// <para>
/// It does NOT embed a dummy string, which spec 7 and spec 14.1's order table both ask for. That
/// is a layer boundary rather than a choice: <c>GenerateAsync</c> acquires a session through
/// <c>IOnnxSessionHost.AcquireAsync</c>, which awaits <c>IEdgeHost.EnsureStartedAsync</c>, and a
/// startup task awaiting startup deadlocks on its own completion. The load path that skips that
/// await, <c>OnnxSessionHost.AcquireCoreAsync</c>, is <c>internal</c> to <c>Qavren.Edge.Onnx</c>
/// and reaches test assemblies only, so nothing in this package can drive it. Closing the gap
/// needs an owner ruling - amend spec 7 and 14.1, or have L0 expose a load path L1 can call.
/// </para>
/// </remarks>
internal sealed class EmbeddingsWarmUpStartupTask(IServiceProvider services, string? name) : IEdgeStartupTask
{
    private static readonly Action<ILogger, string, int, Exception?> s_warmUpCompleted =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.EmbeddingWarmUpCompleted, nameof(EdgeAiEventIds.EmbeddingWarmUpCompleted)),
            "Warm-up parsed the vocabulary for {PresetId}: {VocabularySize} entries.");

    /// <inheritdoc />
    public int Order => EdgeAiStartupOrder.SessionWarmUp;

    /// <summary>The keyed registration this task warms, or null for the unkeyed one.</summary>
    public string? Name => name;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = name is null
            ? services.GetRequiredService<IOptions<OnnxEmbeddingOptions>>()
            : services.GetRequiredKeyedService<IOptions<OnnxEmbeddingOptions>>(name);

        var provider = services.GetRequiredService<IEdgeTokenizerProvider>();
        var tokenizer = await provider.GetAsync(options.Value.Preset, cancellationToken).ConfigureAwait(false);

        var logger = services.GetRequiredService<ILogger<EdgeTokenizerProvider>>();
        s_warmUpCompleted(logger, options.Value.Preset.Id, tokenizer.VocabularySize, null);
    }
}
