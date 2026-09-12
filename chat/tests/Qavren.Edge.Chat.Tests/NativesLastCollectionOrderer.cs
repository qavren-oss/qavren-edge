using Xunit.Sdk;
using Xunit.v3;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Runs every collection whose display name starts with <see cref="NativesPrefix"/> after every
/// other collection, and leaves the relative order of everything else alone.
/// </summary>
/// <remarks>
/// The tier-1 half of this project asserts facts about a process that has not yet touched ORT or
/// ORT GenAI (<c>OrtEnv.IsCreated</c> is false after composition, the order-400 task creates the
/// first <c>OgaHandle</c>). The tier-2 and tier-3 collections load real natives and flip those
/// process-wide flags for good. Ordering the native collections last, with parallelisation
/// disabled in <c>AssemblyInfo.cs</c>, is what keeps both halves honest in one process.
/// </remarks>
public sealed class NativesLastCollectionOrderer : ITestCollectionOrderer
{
    /// <summary>The display-name prefix every collection that loads natives carries.</summary>
    public const string NativesPrefix = "GenAI natives:";

    /// <inheritdoc />
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
    {
        ArgumentNullException.ThrowIfNull(testCollections);

        var ordinary = new List<TTestCollection>();
        var natives = new List<TTestCollection>();

        foreach (var collection in testCollections)
        {
            if (collection.TestCollectionDisplayName.StartsWith(NativesPrefix, StringComparison.Ordinal))
            {
                natives.Add(collection);
            }
            else
            {
                ordinary.Add(collection);
            }
        }

        // Stable within each group, so the native collections keep their declared order too:
        // tier 2 (the committed fixture) before tier 3 (the real model).
        ordinary.Sort(static (a, b) => string.CompareOrdinal(a.TestCollectionDisplayName, b.TestCollectionDisplayName));
        natives.Sort(static (a, b) => string.CompareOrdinal(a.TestCollectionDisplayName, b.TestCollectionDisplayName));

        return [.. ordinary, .. natives];
    }
}
