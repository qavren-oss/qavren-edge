using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.VectorData.Internal;
using Qavren.Edge.VectorData.Tests.Records;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>Builds collection models the same way the store will.</summary>
internal static class ModelFactory
{
    /// <summary>Builds the model for a record type.</summary>
    public static CollectionModel ModelFor<TRecord>(
        string collectionName = "notes",
        Type? keyType = null,
        IEmbeddingGenerator? generator = null,
        ILogger? logger = null,
        VectorStoreCollectionDefinition? definition = null)
        => new EdgeCollectionModelBuilder(collectionName, logger).Build(
            typeof(TRecord),
            keyType ?? typeof(string),
            definition,
            generator ?? new FakeStringEmbeddingGenerator(384));
}
