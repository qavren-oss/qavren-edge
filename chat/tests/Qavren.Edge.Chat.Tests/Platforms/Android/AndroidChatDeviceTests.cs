using Android.Content.PM;
using Android.OS;
using Qavren.Edge.Chat.Tests.Tier2;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec 16.4's Android-only assertions: the merged manifest carries the AAR's contribution, the
/// device profile is <c>SystemWide</c> with a plausible <c>TotalMem</c>, the running ABI is
/// reported and is one GenAI ships a native for, and the natives themselves load the fixture.
/// </summary>
/// <remarks>
/// Compiled only for <c>net10.0-android</c> (the <c>Platforms\**</c> compile-item guard in this
/// project's csproj) and executed only on a device or emulator. It joins the tier-2 collection so
/// it reads the same started container - and the same order-400 profile - every other tier-2
/// class does.
/// </remarks>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class AndroidChatDeviceTests(TinyChatModelFixture fixture)
{
    private const string ChatComponent = "Qavren.Edge.Chat.Onnx";
    private const string TelemetryProvider = "ai.onnxruntime.genai.TelemetryInitializer";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [DeviceFact]
    public void TheMergedManifestCarriesTheAarsPermissionsAndItsTelemetryProvider()
    {
        // The AAR's contribution to EVERY consumer's APK, documented by a test rather than
        // discovered by a reviewer: INTERNET, ACCESS_NETWORK_STATE and the 1DS content provider.
        var context = Application.Context;
        var packageManager = context.PackageManager!;
        var packageName = context.PackageName!;

        PackageInfo? info;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            info = packageManager.GetPackageInfo(
                packageName,
                PackageManager.PackageInfoFlags.Of((long)(PackageInfoFlags.Permissions | PackageInfoFlags.Providers)));
        }
        else
        {
            info = packageManager.GetPackageInfo(packageName, PackageInfoFlags.Permissions | PackageInfoFlags.Providers);
        }

        Assert.NotNull(info);

        var permissions = info.RequestedPermissions ?? [];
        Assert.Contains("android.permission.INTERNET", permissions);
        Assert.Contains("android.permission.ACCESS_NETWORK_STATE", permissions);

        var providers = (info.Providers ?? []).Select(p => p.Name).ToList();
        Assert.Contains(TelemetryProvider, providers);

        JobSummary.Record("android-manifest", "requestedPermissions", string.Join(", ", permissions));
        JobSummary.Record("android-manifest", "providers", string.Join(", ", providers));
    }

    [DeviceFact]
    public void TheDeviceProfileIsSystemWideReportsAPlausibleTotalMemAndTheRunningAbiIsSupported()
    {
        var profile = fixture.Environment.Profile;

        // The distinction the whole budget branches on.
        Assert.Equal(EdgeMemoryBudgetKind.SystemWide, profile.AvailableMemoryKind);
        Assert.Equal(EdgeTotalMemorySource.AndroidActivityManager, profile.TotalMemorySource);

        Assert.NotNull(profile.TotalMemoryBytes);
        Assert.InRange(profile.TotalMemoryBytes.Value, 512L << 20, 64L << 30);

        var abis = Build.SupportedAbis;
        Assert.NotNull(abis);
        Assert.NotEmpty(abis);
        Assert.Equal(abis[0], profile.Abi);

        // The same facts through the public diagnostics keys.
        var chat = Assert.Single(fixture.Diagnostics.Report().Components, c => c.Name == ChatComponent);
        Assert.Equal("SystemWide", chat.Details["availableMemoryKind"]);
        Assert.Equal("True", chat.Details["chatSupportedOnThisAbi"]);
        Assert.Equal(profile.Abi, chat.Details["abi"]);

        // Spec 19 item 10's reading, accumulated run over run.
        JobSummary.Record("android-device", "abi", profile.Abi);
        JobSummary.Record("android-device", "runtimeIdentifier", profile.RuntimeIdentifier);
        JobSummary.Record("android-device", "totalMemoryBytes", profile.TotalMemoryBytes);
        JobSummary.Record("android-device", "isLowRamDevice", profile.IsLowRamDevice);
        JobSummary.Record("android-device", "sdkInt", (int)Build.VERSION.SdkInt);
    }

    [DeviceFact]
    public async Task TheGenAiNativesLoadTheFixtureFromTheAarAndTheProviderIsRecorded()
    {
        using var host = fixture.NewHost();

        await host.Host.PreloadAsync(Token).ConfigureAwait(true);

        var info = host.Host.Describe();
        Assert.NotNull(info);
        Assert.True(info.IsLoaded);
        Assert.True(info.Backend.ChatTemplateSupported);

        // Recorded, never asserted: GenAI is CPU-only on this lane by construction.
        JobSummary.Record("android-device", "providers", string.Join(", ", info.Backend.Providers));
        JobSummary.Record("android-device", "loadMs", info.LoadDuration.TotalMilliseconds);
    }
}
