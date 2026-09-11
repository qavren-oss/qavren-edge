namespace Qavren.Edge;

/// <summary>Stable, programmatically handleable error identity for every <see cref="EdgeException"/>.</summary>
public enum EdgeErrorCode
{
    Unknown = 0,

    DuplicateDatabaseName = 1001,
    NoNativeProviderRegistered = 1002,
    MultipleNativeProvidersRegistered = 1003,
    EncryptionKeyWithoutCipherProvider = 1004,
    EncryptionKeyMissing = 1005,

    NativeLoadFailed = 2001,
    NativeVerificationFailed = 2002,

    MigrationFailed = 3001,
    MigrationVersionConflict = 3002,

    DatabaseKeyRejected = 4001,
}
