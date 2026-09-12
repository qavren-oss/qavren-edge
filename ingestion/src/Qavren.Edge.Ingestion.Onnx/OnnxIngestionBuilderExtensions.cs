using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Onnx.Internal;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Ingestion.Onnx;

/// <summary>The one extension <c>Qavren.Edge.Ingestion.Onnx</c> publishes (spec 11.2).</summary>
public static class OnnxIngestionBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="EdgeChunkTokenizer"/> and <see cref="ResourceMonitorThrottle"/>, and
    /// derives <c>ChunkOptions.MaxTokens</c> / <c>OverlapTokens</c> / <c>MinTokens</c> from the
    /// resolved <see cref="EmbeddingPreset"/> unless the consumer already set them. This is the
    /// call that stops a constant from hiding a truncation. Idempotent; order-independent relative
    /// to <c>AddOnnxEmbeddings</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here resolves lazily, so the builder call touches neither the generator nor the
    /// vocabulary. The preset is read from the generator through
    /// <c>generator.GetService(typeof(EmbeddingPreset))</c> - the same route SP2's own store uses
    /// - and projected onto <see cref="ChunkModelProfile"/>; the projection is applied to every
    /// <c>AddIngestion</c> registration whose <see cref="IngestionOptions.StoreName"/> equals
    /// <paramref name="embeddingsName"/>. Two guards, because a default that silently disagrees
    /// with reality is worse than no default: a <see cref="IngestionOptions.Model"/> the consumer
    /// set explicitly whose <c>Id</c>, <c>Dimensions</c>, <c>MaxSequenceLength</c>,
    /// <c>DocumentPrefix</c> or <c>QueryPrefix</c> differs from the preset is
    /// <see cref="EdgeErrorCode.IngestionOptionsInvalid"/> (6005) naming both, never overwritten;
    /// and a profile left at its default whose collection was declared at a width the preset does
    /// not have is 6005 too, because the DDL is already registered.
    /// </para>
    /// <para>
    /// The tokenizer is built asynchronously by a startup task at order
    /// <see cref="EdgeIngestionStartupOrder.Validate"/> - 10, so the core's order-400 task finds
    /// it already cached and blocks on nothing. Resolving <see cref="IChunkTokenizer"/> before the
    /// host has started still works, and blocks on SP2's provisioning.
    /// </para>
    /// <para>
    /// The throttle registration is appended, so it wins over the core's
    /// <c>FixedIngestionThrottle</c> whichever of <c>AddIngestion</c> and this call comes first. A
    /// consumer's own <c>UseIngestionThrottle</c> wins when it is chained AFTER this call.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="embeddingsName">The keyed <c>AddOnnxEmbeddings</c> registration, or null for the unkeyed one.</param>
    /// <param name="configure">Configures the throttle.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddOnnxIngestion(
        this EdgeBuilder builder,
        string? embeddingsName = null,
        Action<ResourceMonitorThrottleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ResourceMonitorThrottleOptions();
        configure?.Invoke(options);
        if (options.MinBatchSize < 1)
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.IngestionOptionsInvalid,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ResourceMonitorThrottleOptions.MinBatchSize is {0}; it must be positive.",
                    options.MinBatchSize));
        }

        // Idempotent by a descriptor scan on an instance registration, the same idiom SP2's
        // AddOnnxEmbeddings uses: a second call for the same registration adds nothing.
        if (FindBinding(builder.Services, embeddingsName) is not null)
        {
            return builder;
        }

        var binding = new OnnxIngestionBinding(embeddingsName);
        builder.Services.AddSingleton(binding);

        // TryAdd: the core registers no IChunkTokenizer, so a consumer's UseChunkTokenizer wins
        // whether it was chained before this call (this TryAdd no-ops) or after it (last wins).
        builder.Services.TryAddSingleton<IChunkTokenizer>(sp => binding.Resolve(sp));

        // NOT TryAdd: AddIngestion TryAdds FixedIngestionThrottle, and a TryAdd here would lose to
        // it whenever AddIngestion was chained first. Appending wins in both orders.
        builder.Services.AddSingleton<IIngestionThrottle>(sp =>
            new ResourceMonitorThrottle(sp.GetRequiredService<IEdgeResourceMonitor>(), options));

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new OnnxIngestionStartupTask(sp, binding));

        return builder;
    }

    /// <summary>Spec 11.2's projection, verbatim. Every one of the six fields round-trips.</summary>
    /// <param name="preset">The resolved preset.</param>
    /// <returns>The core-side profile.</returns>
    internal static ChunkModelProfile Project(EmbeddingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return new ChunkModelProfile(
            preset.Id, preset.Dimensions, preset.MaxSequenceLength,
            preset.Pooling.ToString(), preset.DocumentPrefix, preset.QueryPrefix);
    }

    /// <summary>
    /// Applies the projection to one <c>AddIngestion</c> registration's options, with spec 11.2's
    /// two guards. Idempotent: a second application against the same preset compares equal and
    /// changes nothing.
    /// </summary>
    /// <param name="options">The registration's options.</param>
    /// <param name="preset">The resolved preset.</param>
    internal static void ApplyPreset(IngestionOptions options, EmbeddingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preset);

        var projected = Project(preset);

        if (ReferenceEquals(options.Model, ChunkModelProfile.MiniLmL6V2Int8))
        {
            // The default profile, untouched. AddIngestion already registered the collection DDL
            // at (Dimensions ?? Model.Dimensions), so the one thing that cannot be re-derived here
            // is the width - a profile swapped in with a different width would hide a 384-wide
            // collection behind a 768-wide recipe, and 6006 would then compare the generator to
            // the swapped profile and see nothing wrong.
            var declared = options.Dimensions ?? options.Model.Dimensions;
            if (declared != projected.Dimensions)
            {
                throw Invalid(string.Format(
                    CultureInfo.InvariantCulture,
                    "Collection '{0}' was declared {1} wide from the default ChunkModelProfile, but the resolved " +
                    "preset '{2}' is {3} wide. Set IngestionOptions.Model (or Dimensions) in AddIngestion to match " +
                    "the preset; the collection DDL is registered there and cannot be re-derived here.",
                    options.CollectionName,
                    declared,
                    preset.Id,
                    projected.Dimensions));
            }

            options.Model = projected;
            return;
        }

        // The consumer typed a profile. Never overwrite it; refuse when it disagrees.
        var typed = options.Model;
        Compare("Id", typed.Id, projected.Id);
        Compare("Dimensions", typed.Dimensions, projected.Dimensions);
        Compare("MaxSequenceLength", typed.MaxSequenceLength, projected.MaxSequenceLength);
        Compare("DocumentPrefix", typed.DocumentPrefix, projected.DocumentPrefix);
        Compare("QueryPrefix", typed.QueryPrefix, projected.QueryPrefix);

        void Compare<T>(string field, T declared, T resolved)
        {
            if (EqualityComparer<T>.Default.Equals(declared, resolved))
            {
                return;
            }

            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "IngestionOptions.Model.{0} is {1} for collection '{2}', but the resolved preset '{3}' declares {4}. " +
                "AddOnnxIngestion never overwrites a value the consumer typed: correct the profile, or leave " +
                "IngestionOptions.Model at its default and let the preset supply it.",
                field,
                Quote(declared),
                options.CollectionName,
                preset.Id,
                Quote(resolved)));
        }
    }

    private static string Quote<T>(T value) => value switch
    {
        null => "null",
        string s => "'" + s + "'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
    };

    private static EdgeConfigurationException Invalid(string message) =>
        new(EdgeErrorCode.IngestionOptionsInvalid, message);

    private static OnnxIngestionBinding? FindBinding(IServiceCollection services, string? embeddingsName)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(OnnxIngestionBinding)
                && services[i].ImplementationInstance is OnnxIngestionBinding binding
                && string.Equals(binding.EmbeddingsName, embeddingsName, StringComparison.Ordinal))
            {
                return binding;
            }
        }

        return null;
    }
}
