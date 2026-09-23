namespace Qavren.Edge.Ingestion;

/// <summary>
/// Base type for every fault Qavren.Edge.Ingestion raises deliberately (spec 13.2). Configuration
/// faults reuse SP1's <see cref="EdgeConfigurationException"/> with a 60xx code, so
/// <c>AddIngestion</c> fails the way every other builder call in the suite fails.
/// </summary>
public class EdgeIngestionException : EdgeException
{
    /// <summary>Creates the exception with the given <paramref name="code"/> and <paramref name="message"/>.</summary>
    /// <param name="code">The stable error identity for this fault.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdgeIngestionException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }

    /// <summary>The source the fault belongs to, when one is in scope.</summary>
    public string? SourceId { get; init; }

    /// <summary>The document the fault belongs to, when one is in scope.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The run the fault belongs to, when one is in scope.</summary>
    public string? RunId { get; init; }

    /// <summary>What the caller should do about it. Mirrored into the reported failure.</summary>
    public string? Remediation { get; init; }

    /// <summary>The size that was refused, for the ceiling faults (6052, 6053).</summary>
    public long? SizeBytes { get; init; }
}

/// <summary>An extractor failed on one document.</summary>
public sealed class EdgeExtractionException : EdgeIngestionException
{
    /// <summary>Creates the exception with the given <paramref name="code"/> and <paramref name="message"/>.</summary>
    /// <param name="code">The stable error identity for this fault.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdgeExtractionException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }

    /// <summary>The extractor's <see cref="IDocumentExtractor.Id"/>.</summary>
    public string? ExtractorName { get; init; }

    /// <summary>The page the fault happened on, for paged formats.</summary>
    public int? PageNumber { get; init; }
}

/// <summary>A chunker violated or could not satisfy the frozen budget.</summary>
public sealed class EdgeChunkingException : EdgeIngestionException
{
    /// <summary>Creates the exception with the given <paramref name="code"/> and <paramref name="message"/>.</summary>
    /// <param name="code">The stable error identity for this fault.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdgeChunkingException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }

    /// <summary>The chunker's <c>Id</c>.</summary>
    public string? ChunkerId { get; init; }

    /// <summary>What the unit actually cost.</summary>
    public int? RequiredTokens { get; init; }

    /// <summary>What the frozen budget allowed.</summary>
    public int? BudgetTokens { get; init; }
}

/// <summary>The persisted ingestion state is missing, corrupt, or of an unsupported version.</summary>
public sealed class EdgeIngestionStateException : EdgeIngestionException
{
    /// <summary>Creates the exception with the given <paramref name="code"/> and <paramref name="message"/>.</summary>
    /// <param name="code">The stable error identity for this fault.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdgeIngestionStateException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }

    /// <summary>The schema version this build writes.</summary>
    public int? ExpectedSchemaVersion { get; init; }

    /// <summary>The schema version found on disk.</summary>
    public int? ActualSchemaVersion { get; init; }
}
