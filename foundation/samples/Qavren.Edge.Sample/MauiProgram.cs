#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Qavren.Edge.Maui;
using Qavren.Edge.Sample.Migrations;
using Qavren.Edge.Sample.Pages;
using Qavren.Edge.Sqlite;
#if QEDGE_CIPHER
using Qavren.Edge.Sqlite.Native.Cipher;
#else
using Qavren.Edge.Sqlite.Native;
#endif

namespace Qavren.Edge.Sample;

public static class MauiProgram
{
    /// <summary>Surfaced by the Encryption page so the UI can say which build it is running in.</summary>
    public const bool IsCipherBuild =
#if QEDGE_CIPHER
        true;
#else
        false;
#endif

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseQavrenEdge(edge => edge
                .AddSqlite(o =>
                {
                    o.DatabaseName = "sample.db";
#if QEDGE_CIPHER
                    // The key never lives in source. SecureStorage holds 32 raw bytes, generated
                    // once on first launch; the raw-key path skips SQLCipher's KDF, which is the
                    // recommended mobile shape (spec 8.8).
                    o.KeyProvider = SampleKey.GetAsync;
#endif
                })
                .AddMigration<M001_CreateNotes>()
#if QEDGE_CIPHER
                .UseSqliteNativeCipher());
#else
                .UseSqliteNative());
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddTransient<DiagnosticsPage>();
        builder.Services.AddTransient<NotesPage>();
        builder.Services.AddTransient<HealthPage>();
        builder.Services.AddTransient<EncryptionPage>();

        return builder.Build();
    }
}
