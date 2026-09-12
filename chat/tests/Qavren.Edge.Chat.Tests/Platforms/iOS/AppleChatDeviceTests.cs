using Qavren.Edge.Chat.Tests.Tier2;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec 16.4's Apple-only assertions: the device profile is <c>PerProcess</c> and its total is
/// the real <c>NSProcessInfo.PhysicalMemory</c>, and the natives - the force-loaded iOS xcframework
/// slice, or whatever Mac Catalyst's RID-graph fallback really does - load the fixture.
/// </summary>
/// <remarks>
/// Compiled only for <c>net10.0-ios</c> and <c>net10.0-maccatalyst</c> (the <c>Platforms\**</c>
/// compile-item guard in this project's csproj). It joins the tier-2 collection so it reads the
/// same started container - and the same order-400 profile - every other tier-2 class does.
/// </remarks>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class AppleChatDeviceTests(TinyChatModelFixture fixture)
{
    private const string ChatComponent = "Qavren.Edge.Chat.Onnx";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [DeviceFact]
    public void TheDeviceProfileIsPerProcessAndMatchesPhysicalMemory()
    {
        var physical = (long)NSProcessInfo.ProcessInfo.PhysicalMemory;
        var profile = fixture.Environment.Profile;

        // The distinction the whole budget branches on: os_proc_available_memory() is a real
        // per-process allowance, so the budget spends it without the system-wide fraction.
        Assert.Equal(EdgeMemoryBudgetKind.PerProcess, profile.AvailableMemoryKind);
        Assert.Equal(EdgeTotalMemorySource.ApplePhysicalMemory, profile.TotalMemorySource);

        Assert.NotNull(profile.TotalMemoryBytes);
        Assert.Equal(physical, profile.TotalMemoryBytes.Value);
        Assert.InRange(profile.TotalMemoryBytes.Value, 1L << 30, 512L << 30);

        // Apple exposes no low-RAM flag and no ABI; "nobody asked" is null, never false.
        Assert.Null(profile.IsLowRamDevice);
        Assert.Null(profile.Abi);

        var chat = Assert.Single(fixture.Diagnostics.Report().Components, c => c.Name == ChatComponent);
        Assert.Equal("PerProcess", chat.Details["availableMemoryKind"]);
        Assert.Equal("True", chat.Details["chatSupportedOnThisAbi"]);

        // Spec 19 item 10's reading, accumulated run over run.
        JobSummary.Record("apple-device", "physicalMemoryBytes", physical);
        JobSummary.Record("apple-device", "runtimeIdentifier", profile.RuntimeIdentifier);
        JobSummary.Record("apple-device", "isMacCatalyst", OperatingSystem.IsMacCatalyst());
        JobSummary.Record("apple-device", "isIOS", OperatingSystem.IsIOS());
    }

    [DeviceFact]
    public async Task TheGenAiNativesLoadTheFixtureAndTheProviderIsRecorded()
    {
        using var host = fixture.NewHost();

        await host.Host.PreloadAsync(Token).ConfigureAwait(true);

        var info = host.Host.Describe();
        Assert.NotNull(info);
        Assert.True(info.IsLoaded);
        Assert.True(info.Backend.ChatTemplateSupported);

        // Recorded, never asserted: both Apple lanes run macos-15-intel with x64 RIDs, so there is
        // no Apple Neural Engine present to assert about even if a provider existed.
        JobSummary.Record("apple-device", "providers", string.Join(", ", info.Backend.Providers));
        JobSummary.Record("apple-device", "loadMs", info.LoadDuration.TotalMilliseconds);
    }
}
