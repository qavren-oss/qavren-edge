using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// ADR 0005: <c>IEdgeModelPaths</c> physically moved to <c>Qavren.Edge.Core</c> at 1.0; this
/// asserts the <c>[TypeForwardedTo]</c> left behind in <c>Qavren.Edge.Onnx</c>
/// (<c>Properties/TypeForwards.cs</c>) still resolves an old-style, assembly-qualified lookup
/// against <c>Qavren.Edge.Onnx</c>, exactly as an already-compiled consumer would perform one.
/// </summary>
public class ModelPathsTypeForwardTests
{
    [Fact]
    public void TheForwardedNameResolvesAgainstQavrenEdgeOnnx()
    {
        var forwarded = Type.GetType(
            "Qavren.Edge.Onnx.IEdgeModelPaths, Qavren.Edge.Onnx", throwOnError: false);

        Assert.NotNull(forwarded);
        Assert.Equal(typeof(IEdgeModelPaths), forwarded);
    }
}
