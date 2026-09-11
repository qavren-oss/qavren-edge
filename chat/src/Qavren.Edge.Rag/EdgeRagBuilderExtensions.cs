using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Rag.Internal;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Rag;

/// <summary>Registration for the RAG recipe.</summary>
public static class EdgeRagBuilderExtensions
{
    private const string TrimMessage = "MEVD reflects over TRecord to build a collection model.";

    /// <summary>
    /// Registers <see cref="RagOptions"/> and the diagnostics contributor. Registers <b>no</b>
    /// retriever and <b>no</b> chat client - both are the consumer's choice, which is what makes
    /// this package work with either. Idempotent.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Configures <see cref="RagOptions"/>.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddEdgeRag(this EdgeBuilder builder, Action<RagOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions();

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, RagDiagnosticsContributor>());

        return builder;
    }

    /// <summary>Registers <typeparamref name="TRetriever"/> as the singleton <see cref="IEdgeRetriever"/>.</summary>
    /// <typeparam name="TRetriever">The retriever implementation.</typeparam>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddRetriever<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRetriever>(
        this EdgeBuilder builder)
        where TRetriever : class, IEdgeRetriever
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEdgeRag();
        builder.Services.AddSingleton<IEdgeRetriever, TRetriever>();
        return builder;
    }

    /// <summary>Registers a singleton <see cref="IEdgeRetriever"/> from a factory.</summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="factory">Builds the retriever.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddRetriever(this EdgeBuilder builder, Func<IServiceProvider, IEdgeRetriever> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.AddEdgeRag();
        builder.Services.AddSingleton(factory);
        return builder;
    }

    /// <summary>
    /// Calls <see cref="AddEdgeRag"/>, resolves MEVD's abstract <c>VectorStore</c> from DI and takes
    /// <c>GetCollection&lt;TKey, TRecord&gt;(collectionName)</c>.
    /// </summary>
    /// <typeparam name="TKey">The collection's key type.</typeparam>
    /// <typeparam name="TRecord">The collection's record type.</typeparam>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="collectionName">The MEVD collection to retrieve from.</param>
    /// <param name="project">
    /// The one projector shape in this package: <c>Func&lt;TRecord, RagSource&gt;</c>, the same
    /// signature <see cref="VectorStoreRetriever{TKey, TRecord}"/>'s constructor takes.
    /// </param>
    /// <param name="configure">Configures the retriever.</param>
    /// <param name="options">Configures <see cref="RagOptions"/>.</param>
    /// <param name="storeName">
    /// Null resolves the <b>unkeyed</b> <c>MEVD.VectorStore</c>; a name resolves the <b>keyed</b>
    /// one. The parameter exists because sub-project 2 registers both - <c>AddVectorStore()</c>
    /// uses <c>AddSingleton&lt;VectorStore&gt;</c> and <c>AddVectorStore(name, ...)</c> uses
    /// <c>AddKeyedSingleton&lt;VectorStore&gt;</c>. Resolution is <b>lazy</b>: a name with no such
    /// keyed registration fails when the retriever is first resolved, naming the key, not at
    /// registration time - because pretending otherwise would mis-describe when the app breaks.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    public static EdgeBuilder AddVectorStoreRetriever<TKey, TRecord>(
        this EdgeBuilder builder,
        string collectionName,
        Func<TRecord, RagSource> project,
        Action<VectorStoreRetrieverOptions<TRecord>>? configure = null,
        Action<RagOptions>? options = null,
        string? storeName = null)
        where TKey : notnull
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(project);

        builder.AddEdgeRag(options);

        builder.Services.AddSingleton<IEdgeRetriever>(serviceProvider =>
        {
            var store = storeName is null
                ? serviceProvider.GetRequiredService<MEVD.VectorStore>()
                : serviceProvider.GetKeyedService<MEVD.VectorStore>(storeName)
                    ?? throw new EdgeRagException(
                        EdgeErrorCode.RagRetrieverMissing,
                        $"No keyed Microsoft.Extensions.VectorData.VectorStore is registered under " +
                        $"the service key '{storeName}'.",
                        collectionName: collectionName,
                        remediation:
                            "Register the store with AddVectorStore(name, ...) under that same key, " +
                            "or pass storeName: null to AddVectorStoreRetriever to use the unkeyed store.");

            var collection = store.GetCollection<TKey, TRecord>(collectionName);
            return new VectorStoreRetriever<TKey, TRecord>(collection, project, configure);
        });

        return builder;
    }

    /// <summary>
    /// Registers <see cref="ExtractiveChatClient"/> as the <c>IChatClient</c>, for an app that
    /// ships no weights.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Configures <see cref="ExtractiveChatOptions"/>.</param>
    /// <param name="pipeline">
    /// The MEAI pipeline over it - <c>c =&gt; c.UseRag()</c> is the no-LLM floor with citations.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddExtractiveChat(
        this EdgeBuilder builder,
        Action<ExtractiveChatOptions>? configure = null,
        Action<ChatClientBuilder>? pipeline = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEdgeRag();

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        var chatBuilder = builder.Services.AddChatClient(serviceProvider => new ExtractiveChatClient(
            serviceProvider.GetService<IOptions<ExtractiveChatOptions>>()?.Value,
            serviceProvider.GetService<ILoggerFactory>()));

        pipeline?.Invoke(chatBuilder);
        return builder;
    }
}
