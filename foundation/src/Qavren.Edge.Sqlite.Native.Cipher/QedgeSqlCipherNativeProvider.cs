namespace Qavren.Edge.Sqlite.Native.Cipher;

/// <summary>
/// Same install path as the plain provider, redirected to <c>qedge_sqlcipher</c> and reporting the
/// library name as <c>sqlcipher</c> so Microsoft.Data.Sqlite's known-library table returns
/// <see langword="true"/> and its <c>Password</c> path is enabled.
/// </summary>
public sealed class QedgeSqlCipherNativeProvider : QedgeSqliteNativeProvider
{
    /// <inheritdoc/>
    public override string Name => "Qavren.Edge.Sqlite.Native.Cipher";

    /// <inheritdoc/>
    public override bool SupportsEncryption => true;

    /// <inheritdoc/>
    protected override string ReportedLibraryName => "sqlcipher";

    /// <inheritdoc/>
    protected override string PhysicalLibraryName => "qedge_sqlcipher";
}
