// ADR 0005 (embeddings/docs/adr/0005-absorb-iedgemodelpaths-into-sp1-at-1-0.md) moves this
// interface's *declaration* here at 1.0, but keeps its namespace as `Qavren.Edge.Onnx` rather than
// renamespacing it to `Qavren.Edge` to match this project's folder convention. A type-forward
// (`Qavren.Edge.Onnx/Properties/TypeForwards.cs`) requires the identical fully-qualified name on
// both sides, so the namespace is binary-compatibility surface, not a naming choice made here.

namespace Qavren.Edge.Onnx;

/// <summary>
/// Where a 23-137 MB re-derivable blob belongs. Deliberately NOT <see cref="IEdgePaths.Data"/>
/// (backed up on all three platforms; Android's Auto Backup quota is 25 MB per app, so one model
/// there consumes the whole quota and stops the user's real database being backed up) and
/// deliberately NOT <see cref="IEdgePaths.Cache"/> (the OS may purge it mid-session, converting a
/// purge into a re-download).
/// </summary>
/// <remarks>
/// The interface lives here, in <c>Qavren.Edge.Core</c>, but its only shipped implementation,
/// <c>DefaultEdgeModelPaths</c>, stays in <c>Qavren.Edge.Onnx</c> alongside the Android
/// (<c>NoBackupFilesDir</c>) and Apple (<c>NSURL</c> with backup exclusion) implementations —
/// those are platform-specific and this project targets <c>net10.0</c> only. The durable,
/// not-backed-up, never-OS-purged semantics above are what every implementation, on every
/// platform, has to provide.
/// </remarks>
public interface IEdgeModelPaths
{
    /// <summary>Durable, excluded from cloud backup, never OS-purged. Created on first access.</summary>
    string Models { get; }

    /// <summary>Sibling of <see cref="Models"/>. Owned and purged by this library, never by the OS.</summary>
    string OrtCache { get; }
}
