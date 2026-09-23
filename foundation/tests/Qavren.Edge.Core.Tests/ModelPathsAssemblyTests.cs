using Xunit;

namespace Qavren.Edge.Core.Tests;

/// <summary>
/// ADR 0005: at 1.0, <c>IEdgeModelPaths</c> physically moved here, to <c>Qavren.Edge.Core</c>,
/// with a type-forward left behind in <c>Qavren.Edge.Onnx</c> (see
/// <c>Qavren.Edge.Onnx.Tests.ModelPathsTypeForwardTests</c> for the forward-side assertion).
/// </summary>
public class ModelPathsAssemblyTests
{
    [Fact]
    public void IEdgeModelPaths_IsDeclaredInQavrenEdgeCore()
    {
        Assert.Equal("Qavren.Edge.Core", typeof(Qavren.Edge.Onnx.IEdgeModelPaths).Assembly.GetName().Name);
    }
}
