using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData.Internal;

namespace Qavren.Edge.VectorData;

/// <summary>
/// The trim/AOT-safe path, and a first-class one: the conformance suite runs against it too, and
/// the <c>trim-smoke</c> job publishes it.
/// <para>
/// Its constructor carries <b>no</b> trim annotations, because it chains to the
/// <c>CollectionModel</c>-taking base constructor rather than the reflecting one, and builds its
/// model with <c>CollectionModelBuilder.BuildDynamic</c>, which reflects over nothing. (MEVD's
/// reflection-based <c>Build</c> throws for <see cref="Dictionary{TKey, TValue}"/> by design.)
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name mirrors the MEVD base type VectorStoreCollection<TKey, TRecord> this derives from. Renaming it would break the one-to-one reading of the provider against the abstraction it implements.")]
public sealed class EdgeDynamicVectorStoreCollection
    : EdgeVectorStoreCollection<object, Dictionary<string, object?>>
{
    /// <summary>Creates the collection.</summary>
    /// <param name="database">The sub-project 1 database this collection lives in.</param>
    /// <param name="name">The collection name.</param>
    /// <param name="options">
    /// Per-collection overrides. <see cref="Microsoft.Extensions.VectorData.VectorStoreCollectionOptions.Definition"/>
    /// must be non-null: a dynamic record carries no attributes to reflect over.
    /// </param>
    public EdgeDynamicVectorStoreCollection(
        IEdgeDatabase database,
        string name,
        EdgeVectorStoreCollectionOptions options)
        : base(database, name, BuildDynamicModel(name, options), options)
    {
    }

    private static CollectionModel BuildDynamicModel(string name, EdgeVectorStoreCollectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var definition = options.Definition
            ?? throw new ArgumentException(
                "A dynamic collection needs a VectorStoreCollectionDefinition: Dictionary<string, object?> carries " +
                "no attributes to reflect over. Set EdgeVectorStoreCollectionOptions.Definition.",
                nameof(options));

        return new EdgeCollectionModelBuilder(name).BuildDynamic(definition, options.EmbeddingGenerator);
    }
}
