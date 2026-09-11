namespace Qavren.Edge.Embeddings.Tests.Fakes;

/// <summary>
/// A TimeProvider frozen at one instant. Five lines, no package, no CPM edit:
/// Microsoft.Extensions.TimeProvider.Testing is not in Directory.Packages.props, SP1's own
/// LifecycleHubTests passes TimeProvider.System rather than faking one, and adding a CPM entry in
/// wave 4 would be an integrator-owned root-file edit a whole wave after the prologue that owns them.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
