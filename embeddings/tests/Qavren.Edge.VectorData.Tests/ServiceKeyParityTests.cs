using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// One line, and it is the entire defence against a duplicated literal drifting.
/// </summary>
public sealed class ServiceKeyParityTests
{
    [Fact]
    public void TheQueryGeneratorServiceKeysAreTheSameLiteral()
    {
        // The literal is duplicated because spec 2 decision 2 forbids Qavren.Edge.VectorData from
        // referencing Qavren.Edge.Embeddings.Onnx: the store has to work with Azure OpenAI
        // embeddings and no ONNX at all, a shared constant would need a third assembly or an edit to
        // sub-project 1, and spec 5 allows neither. This project is the only place in the suite
        // where both symbols are visible at once, which is why the assertion lives here.
        Assert.Equal(
            Qavren.Edge.Embeddings.Onnx.EdgeEmbeddings.QueryServiceKey,
            Qavren.Edge.VectorData.EdgeVectorData.QueryGeneratorServiceKey);
    }
}
