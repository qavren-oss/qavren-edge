namespace Qavren.Edge.VectorData;

/// <summary>How the keywords of a hybrid search are combined into one FTS5 MATCH expression.</summary>
public enum KeywordCombinator
{
    /// <summary>Any keyword matches. The default.</summary>
    Or = 0,

    /// <summary>Every keyword must match.</summary>
    And = 1,
}

/// <summary>Constants shared by this package and the apps that wire it up.</summary>
public static class EdgeVectorData
{
    /// <summary>
    /// The <c>GetService</c> service key under which an embedding generator may expose a
    /// query-prefixed sibling. The store asks every generator it is handed for one and falls back
    /// to the generator itself.
    /// <para>
    /// This is a duplicate of <c>Qavren.Edge.Embeddings.Onnx</c>'s
    /// <c>EdgeEmbeddings.QueryServiceKey</c>, and duplicated <b>on purpose</b>. Spec §2 decision 2
    /// keeps this package free of any ONNX reference, so the store works with Azure OpenAI
    /// embeddings and no ONNX at all - which means the symbol over there is not visible here. A
    /// shared constant would need a third assembly or an edit to sub-project 1, and spec §5 allows
    /// neither. Do not "de-duplicate" this: the two literals are asserted equal by the one test
    /// project that references both packages (spec §16.2).
    /// </para>
    /// </summary>
    public const string QueryGeneratorServiceKey = "qavren.edge.query";
}
