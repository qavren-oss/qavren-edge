namespace Qavren.Edge.Rag;

/// <summary>
/// A retrieval-augmented generation failure: the 7200-7299 range. Carries the retriever name, the
/// collection name and a remediation sentence, because all three are what a caller needs and none
/// of them survive a bare message.
/// </summary>
public sealed class EdgeRagException : EdgeException
{
    /// <summary>Creates a RAG failure carrying whichever of the three context fields are known.</summary>
    public EdgeRagException(
        EdgeErrorCode code,
        string message,
        string? retrieverName = null,
        string? collectionName = null,
        string? remediation = null,
        Exception? innerException = null)
        : base(code, Build(message, retrieverName, collectionName, remediation), innerException)
    {
        RetrieverName = retrieverName;
        CollectionName = collectionName;
        Remediation = remediation;
    }

    /// <summary><see cref="IEdgeRetriever.Name"/> of the retriever that failed, when there was one.</summary>
    public string? RetrieverName { get; }

    /// <summary>The MEVD collection behind it, when the failure came from the vector-store adapter.</summary>
    public string? CollectionName { get; }

    /// <summary>What the caller should do about it.</summary>
    public string? Remediation { get; }

    private static string Build(string message, string? retriever, string? collection, string? remediation)
    {
        var text = message;

        if (!string.IsNullOrEmpty(retriever))
        {
            text += $" Retriever: '{retriever}'.";
        }

        if (!string.IsNullOrEmpty(collection))
        {
            text += $" Collection: '{collection}'.";
        }

        if (!string.IsNullOrEmpty(remediation))
        {
            text += " Remediation: " + remediation;
        }

        return text;
    }
}
