namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// A <see cref="ChatModelInfo"/> built without a model, for the assertions that are about what the
/// record <i>carries</i> rather than about what the loader <i>did</i>.
/// </summary>
internal static class TestInfo
{
    /// <summary>Builds a description of a model that is not loaded.</summary>
    /// <param name="preset">The preset it would describe.</param>
    /// <returns>The description.</returns>
    public static ChatModelInfo Build(ChatPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return new ChatModelInfo(
            preset.Id,
            preset.Manifest.ModelId,
            Directory: "(none)",
            DirectorySha16: "0000000000000000",
            preset.Shape,
            new ChatMemoryDecision(
                ChatMemoryVerdict.Allowed,
                preset.DefaultMaxContextTokens,
                RequiredBytes: 0,
                preset.Shape.WeightsBytes,
                KvCacheBytes: 0,
                WorkspaceBytes: 0,
                ReserveBytes: 0,
                AvailableBytes: null,
                UsableBytes: null,
                TotalMemoryBytes: null,
                EdgeMemoryBudgetKind.Unknown,
                UsedMeasuredPeak: false,
                Explanation: "(test)"),
            preset.DefaultMaxContextTokens,
            new ChatBackendReport(
                GenAiVersion: "0.15.2",
                OrtVersion: "1.30.0.0",
                RuntimeIdentifier: "win-x64",
                Abi: null,
                Providers: ["cpu"],
                ChatTemplateSupported: true,
                EdgeGuidanceProbeResult.Unprobed,
                PromptFormatter: "(test)"),
            TimeSpan.Zero,
            DateTimeOffset.UnixEpoch,
            LoadCount: 1,
            ActiveLeases: 0,
            IsLoaded: false);
    }
}
