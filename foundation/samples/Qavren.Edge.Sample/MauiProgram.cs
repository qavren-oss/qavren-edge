#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Qavren.Edge.Chat;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Maui;
using Qavren.Edge.Onnx;
using Qavren.Edge.Rag;
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

    /// <summary>
    /// Preferences key for the Chat page's "simulate a tight device" toggle (SP4 spec 18: a
    /// <c>ChatInsufficientMemory</c> refusal rendered in full rather than swallowed). The memory
    /// gate's options are read once, when <c>AddOnnxChat</c>'s configure callback runs, so like
    /// the Diagnostics toggles this persists and applies on next launch. When set,
    /// <see cref="ChatMemoryBudgetOptions.Override"/> feeds the REAL gate a fake 256 MiB
    /// available-memory reading - the refusal text is the gate's own words at that number, not a
    /// string the sample made up.
    /// </summary>
    public const string SimulateTightDevicePreferenceKey = "Qavren.Edge.Sample.SimulateTightDevice";

    /// <summary>The fake available-memory reading the tight-device toggle feeds the gate.</summary>
    public const long SimulatedAvailableMemoryBytes = 256L * 1024 * 1024;

    public static MauiApp CreateMauiApp()
    {
        var profileComputePlan = Preferences.Default.Get(ProfileComputePlanPreferenceKey, false);
        var includeRowCounts = Preferences.Default.Get(IncludeRowCountsPreferenceKey, false);
        var simulateTightDevice = Preferences.Default.Get(SimulateTightDevicePreferenceKey, false);

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
                .AddVectorCollectionMigration<string, Note>(version: 2, NotesCollectionName)
                // SP4 spec 4.3 / 18: chat with retrieval over the Search page's notes. AddOnnxChat
                // calls AddOnnx() for you (idempotent - one OrtEnv task shared with the embeddings
                // above), and UseRag() resolves the retriever registered below at resolve time, so
                // the order of these two calls is irrelevant.
                .AddOnnxChat(
                    ChatPresets.Llama32_1BInstructInt4,
                    configure: o =>
                    {
                        // The metered-network hook, in one line. SP4 does not read connectivity
                        // for you (spec 12.2.1): a 1.24 GB transfer on a metered link is the
                        // app's decision, so the library asks and the app answers. (Spec 18
                        // writes `ConnectionProfile == ConnectionProfile.WiFi`; MAUI's
                        // IConnectivity exposes ConnectionProfiles, a set - a device can be on
                        // Wi-Fi and cellular at once - so the real one-liner is a Contains.)
                        o.Provisioning.IsTransferPermitted =
                            () => Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

                        if (simulateTightDevice)
                        {
                            // Chat page toggle: the whole gate, run over a reading of 256 MiB
                            // free, so the refusal that comes back is the real gate's own
                            // explanation. A fresh options instance so the override does not
                            // re-enter itself.
                            var gate = new ChatMemoryBudgetOptions();
                            o.Memory.Override = request => ChatMemoryBudget.Resolve(
                                request with
                                {
                                    Resources = request.Resources with
                                    {
                                        AvailableMemoryBytes = SimulatedAvailableMemoryBytes,
                                    },
                                },
                                gate);
                        }
                    },
                    pipeline: chat => chat.UseRag())
                // Spec 4.3's projector, over this sample's Note (spec 18: "the existing Search
                // page's notes"). The record has no Url, so no Uri is set; the collection is
                // NotesCollectionName rather than the spec's literal "notes" for the reason on
                // that constant.
                .AddVectorStoreRetriever<string, Note>(
                    NotesCollectionName,
                    n => new RagSource(n.Key, n.Body) { Title = n.Title }));

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddTransient<DiagnosticsPage>();
        builder.Services.AddTransient<NotesPage>();
        builder.Services.AddTransient<HealthPage>();
        builder.Services.AddTransient<EncryptionPage>();
        builder.Services.AddTransient<EmbeddingsPage>();
        builder.Services.AddTransient<SearchPage>();
        builder.Services.AddTransient<ChatPage>();
        builder.Services.AddTransient<AskPage>();

        return builder.Build();
    }
}
