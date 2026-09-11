using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Qavren.Edge.Rag;

/// <summary>The one line that turns any <c>IChatClient</c> pipeline into a RAG pipeline.</summary>
public static class RagChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="RagChatClient"/> to the pipeline.
    /// </summary>
    /// <param name="builder">The MEAI pipeline builder.</param>
    /// <param name="retriever">
    /// Null resolves <see cref="IEdgeRetriever"/> as <b>required</b> from DI, matching
    /// <c>UseDistributedCache</c>'s convention. A missing one is
    /// <see cref="EdgeErrorCode.RagRetrieverMissing"/> (7201), naming the two registrations that
    /// would have supplied it.
    /// </param>
    /// <param name="configure">
    /// Applied on top of whatever <c>AddEdgeRag</c> configured, to a <b>fresh</b>
    /// <see cref="RagOptions"/> built by the container's own options factory - so one pipeline's
    /// overrides never leak into another's.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    public static ChatClientBuilder UseRag(
        this ChatClientBuilder builder,
        IEdgeRetriever? retriever = null,
        Action<RagOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use((innerClient, services) =>
        {
            var resolved = retriever
                ?? services.GetService<IEdgeRetriever>()
                ?? throw new EdgeRagException(
                    EdgeErrorCode.RagRetrieverMissing,
                    "UseRag() was given no retriever and none is registered.",
                    remediation:
                        "Call AddVectorStoreRetriever<TKey, TRecord>(collectionName, project) or " +
                        "AddRetriever(...) on the EdgeBuilder, or pass the retriever to UseRag() " +
                        "directly.");

            var options = services.GetService<IOptionsFactory<RagOptions>>() is { } factory
                ? factory.Create(Microsoft.Extensions.Options.Options.DefaultName)
                : new RagOptions();

            configure?.Invoke(options);

            return new RagChatClient(innerClient, resolved, options, services.GetService<ILoggerFactory>());
        });
    }
}
