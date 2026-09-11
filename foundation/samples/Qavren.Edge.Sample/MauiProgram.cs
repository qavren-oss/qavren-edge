#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Maui;
using Qavren.Edge.Onnx;
using Qavren.Edge.Sample.Migrations;
using Qavren.Edge.Sample.Models;
using Qavren.Edge.Sample.Pages;
using Qavren.Edge.Sqlite;
#if QEDGE_CIPHER
using Qavren.Edge.Sqlite.Native.Cipher;
#else
using Qavren.Edge.Sqlite.Native;
#endif
using Qavren.Edge.VectorData;

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

    /// <summary>
    /// Spec 18's Search page collection name. NOT "notes" - SP1's sample already owns a plain
    /// "notes" table via <see cref="M001_CreateNotes"/>, with a different shape entirely. See
    /// <see cref="Note"/>.
    /// </summary>
    public const string NotesCollectionName = "embedding_notes";

    /// <summary>
    /// Preferences keys for the Diagnostics page's two toggles (spec 18). Both options
    /// (<c>CoreMlProviderOptions.ProfileComputePlan</c>, <c>EdgeVectorStoreOptions.
    /// IncludeRowCountsInDiagnostics</c>) are read once, at first resolution of the registration
    /// they belong to, and never re-read afterwards - there is no library-exposed seam for a live
    /// runtime flip. So the toggles persist to <see cref="Preferences"/> and apply on next launch;
    /// the Diagnostics page says so.
    /// </summary>
    public const string ProfileComputePlanPreferenceKey = "Qavren.Edge.Sample.ProfileComputePlan";

    public const string IncludeRowCountsPreferenceKey = "Qavren.Edge.Sample.IncludeRowCountsInDiagnostics";

    public static MauiApp CreateMauiApp()
    {
        var profileComputePlan = Preferences.Default.Get(ProfileComputePlanPreferenceKey, false);
        var includeRowCounts = Preferences.Default.Get(IncludeRowCountsPreferenceKey, false);

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
                .UseSqliteNativeCipher()
#else
                .UseSqliteNative()
#endif
                // Diagnostics page toggle 1 of 2: off by default, applies to every session this
                // policy governs (spec 9.2). Set BEFORE AddOnnxEmbeddings so the first model
                // registration to resolve already sees it, though order does not matter here -
                // IOptions<OnnxExecutionProviderPolicy> merges every Configure call at first resolve.
                .UseExecutionProviderPolicy(o => o.CoreMl.ProfileComputePlan = profileComputePlan)
                // Spec 4.3's four-call shape: the UNKEYED registration is what AddVectorStore
                // resolves for Note.Embedding (spec 4.3's mechanism, §12.5).
                .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
                // The Embeddings page's other three picker entries (spec 18: "pick a preset"). Kept
                // OUT of the unkeyed slot the vector store reads, per the keyed-registration
                // convention ("so two presets can coexist in one process") - nothing here changes
                // what Search page notes embed with.
                .AddOnnxEmbeddings(
                    EmbeddingPresets.MiniLmL6V2Fp32.Id,
                    o => o.Preset = EmbeddingPresets.MiniLmL6V2Fp32)
                .AddOnnxEmbeddings(EmbeddingPresets.BgeSmallEnV15.Id, o => o.Preset = EmbeddingPresets.BgeSmallEnV15)
                .AddOnnxEmbeddings(
                    EmbeddingPresets.NomicEmbedTextV15Int8.Id,
                    o => o.Preset = EmbeddingPresets.NomicEmbedTextV15Int8)
                // Diagnostics page toggle 2 of 2: one query per collection, off by default.
                .AddVectorStore(o => o.IncludeRowCountsInDiagnostics = includeRowCounts)
                // Version 2: M001_CreateNotes above already claims version 1 in this database.
                .AddVectorCollectionMigration<string, Note>(version: 2, NotesCollectionName));

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddTransient<DiagnosticsPage>();
        builder.Services.AddTransient<NotesPage>();
        builder.Services.AddTransient<HealthPage>();
        builder.Services.AddTransient<EncryptionPage>();
        builder.Services.AddTransient<EmbeddingsPage>();
        builder.Services.AddTransient<SearchPage>();

        return builder.Build();
    }
}
