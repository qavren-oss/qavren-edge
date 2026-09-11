using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Rag.Internal;

/// <summary>
/// Spec 14.3's <c>"Qavren.Edge.Rag"</c> block. The component name contains no "Native", so
/// sub-project 1's <c>EdgeDiagnostics.Report()</c> keeps picking the SQLite native block for
/// <c>EdgeDiagnosticsReport.Native</c>.
/// </summary>
/// <remarks>
/// It reaches the live middleware through <c>IChatClient.GetService</c> rather than through a
/// registration, because what a reader needs is the pipeline the app actually built. <b>Every</b>
/// read renders a throw as spec 14.3's <c>"(unavailable: &lt;TypeName&gt;)"</c> sentinel rather
/// than as a null - including the <i>resolutions</i> the other reads are taken through, because a
/// factory that throws (an <c>AddRetriever(factory)</c> whose factory fails) would otherwise make
/// the whole block read "not registered", which is a different and untrue statement. A diagnostics
/// report must never be the thing that throws, and a reader must still be able to tell a read that
/// failed from a service that was never registered.
/// </remarks>
internal sealed class RagDiagnosticsContributor : IEdgeDiagnosticsContributor
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the contributor.</summary>
    /// <param name="services">The container the pipeline was resolved from.</param>
    public RagDiagnosticsContributor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.Rag";

    /// <inheritdoc />
    public string? ComponentVersion =>
        typeof(RagDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        // Each service is resolved once - a transient registration would otherwise be built once
        // per read - and the outcome of that resolution is carried, so a throw becomes the
        // sentinel on every read taken through it instead of a null.
        var retriever = Resolve<IEdgeRetriever>();
        var chatClient = Resolve<IChatClient>();
        var rag = Derive(chatClient, c => c.GetService(typeof(RagChatClient)) as RagChatClient);
        var extractive = Derive(chatClient, c => c.GetService(typeof(ExtractiveChatClient)) as ExtractiveChatClient);
        var statistics = Derive(rag, r => r.Statistics);

        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["retriever"] = Read(retriever, r => r.Name),
            ["retrieverType"] = Read(retriever, r => r.GetType().FullName),
            ["collectionName"] = Read(retriever, r => (r as IVectorStoreRetrieverDescriptor)?.CollectionName),
            ["top"] = SafeText(() => Text(ResolveOptions().Top)),
            ["maxContextTokens"] = SafeText(() => Text(ResolveOptions().MaxContextTokens)),
            ["preferHybridSearch"] =
                Read(retriever, r => (r as IVectorStoreRetrieverDescriptor)?.PrefersHybridSearch.ToString()),

            // Not through Read: "no chat client registered" is reportable as False, where a null
            // would read as "this key is not applicable". A failed resolve still renders the
            // sentinel, because "False" would be an outright false statement.
            ["chatClientPresent"] = chatClient.Failure ?? (chatClient.Value is not null).ToString(),

            ["asks"] = Read(statistics, s => Text(s.Asks)),
            ["lastRetrievedCount"] = Read(statistics, s => Text(s.LastRetrievedCount)),
            ["lastRetrievalMs"] =
                Read(statistics, s => s.LastRetrievalMs.ToString("0.###", CultureInfo.InvariantCulture)),
            ["lastScoreKind"] = Read(statistics, s => s.LastScoreKind?.ToString()),
            ["lastContextTokens"] = Read(statistics, s => Text(s.LastContextTokens)),
            ["lastCitationsAttached"] = Read(statistics, s => Text(s.LastCitationsAttached)),
            ["lastCitationsUnresolved"] = Read(statistics, s => Text(s.LastCitationsUnresolved)),
            ["lastGrounded"] = Read(statistics, s => s.LastGrounded.ToString()),
            ["retrievalFailures"] = Read(statistics, s => Text(s.RetrievalFailures)),
            ["extractiveAnswers"] = Read(extractive, e => Text(e.Statistics.ExtractiveAnswers)),
        };

        return details;
    }

    // Read inside SafeText rather than once up front, so a container that throws while resolving
    // the options renders as the sentinel instead of silently reporting the compiled defaults.
    private RagOptions ResolveOptions() =>
        _services.GetService<IOptions<RagOptions>>()?.Value ?? new RagOptions();

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The outcome of one resolution: a value (possibly null, meaning "not registered") or the
    /// sentinel text of the throw that stopped it. The two are never confused, which is the whole
    /// point of spec 14.3's sentinel.
    /// </summary>
    private readonly record struct Resolved<T>(T? Value, string? Failure)
        where T : class;

    private Resolved<T> Resolve<T>()
        where T : class
    {
        try
        {
            return new Resolved<T>(_services.GetService<T>(), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Resolved<T>(null, Sentinel(ex));
        }
    }

    // Derives one resolution from another: a failure upstream stays a failure, so every read below
    // it renders the sentinel; a null upstream stays a null, which is "not registered".
    private static Resolved<TNext> Derive<T, TNext>(Resolved<T> source, Func<T, TNext?> read)
        where T : class
        where TNext : class
    {
        if (source.Failure is not null)
        {
            return new Resolved<TNext>(null, source.Failure);
        }

        if (source.Value is null)
        {
            return new Resolved<TNext>(null, null);
        }

        try
        {
            return new Resolved<TNext>(read(source.Value), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Resolved<TNext>(null, Sentinel(ex));
        }
    }

    // One shape for every report value: the sentinel when the resolution or the read threw, null
    // when nothing was registered, the text otherwise.
    private static string? Read<T>(Resolved<T> resolved, Func<T, string?> read)
        where T : class =>
        resolved.Failure ?? (resolved.Value is null ? null : SafeText(() => read(resolved.Value)));

    // Spec 14.3: a read that throws renders as "(unavailable: <TypeName>)", never as null - null
    // belongs to "not registered", and a report reader has to be able to tell the two apart.
    private static string? SafeText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Sentinel(ex);
        }
    }

    private static string Sentinel(Exception exception) =>
        string.Create(CultureInfo.InvariantCulture, $"(unavailable: {exception.GetType().Name})");
}
