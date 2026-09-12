namespace Qavren.Edge.Ingestion;

/// <summary>
/// The order SP3's single startup task runs at. It opens no database; it resolves the tokenizer
/// and freezes the chunk budget (spec 8.1, 11.1 step 5).
/// </summary>
public static class EdgeIngestionStartupOrder
{
    public const int Validate = 400;
}
