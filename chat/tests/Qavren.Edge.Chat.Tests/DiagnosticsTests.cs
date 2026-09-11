using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Diagnostics;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>Spec section 14.3's chat block, key by key.</summary>
public class DiagnosticsTests
{
    /// <summary>
    /// Every key section 14.3 lists, written out here rather than read from the contributor.
    /// </summary>
    /// <remarks>
    /// A "present" check that reads the contributor's own dictionary keys proves nothing: it would
    /// pass for any set of keys at all, including an empty one. This list is the contract.
    /// </remarks>
    private static readonly string[] ExpectedKeys =
    [
        "serviceKey",

        // Environment
        "genAiVersion", "genAiManagedAsset", "ortVersionInProcess", "ortEnvCreatedBeforeGenAi",
        "ogaHandleOwned", "telemetryDisabled", "runtimeIdentifier", "abi", "chatSupportedOnThisAbi",
        "totalMemoryBytes", "availableMemoryKind", "isLowRamDevice", "chatIntraOpNumThreads",

        // Model
        "presetId", "modelId", "displayName", "spdxLicense", "huggingFaceRepo",
        "huggingFaceRevision", "modelDirectory", "provisioned", "bundleBytes", "weightsBytes",
        "freeDiskBytes", "modelType", "configContextLength", "vocabSize", "numHiddenLayers",
        "numKeyValueHeads", "headSize", "slidingWindow", "kvBytesPerToken", "kvBytesPerElement",
        "providers", "chatTemplateSupported", "promptFormatter", "guidanceEnforced", "loaded",
        "loadMs", "loadCount", "leases",

        // Budget
        "budgetVerdict", "budgetExplanation", "requestedContextTokens", "resolvedContextTokens",
        "requiredBytes", "kvCacheBytes", "workspaceBytes", "reserveBytes", "availableBytes",
        "usableBytes", "usedMeasuredPeak", "measuredPeakBytes", "measuredOn",

        // Runtime honesty
        "turns", "rejectedTurns", "tokensGenerated", "tokensPerSecondP50", "tokensPerSecondP95",
        "lastTokensPerSecond", "lastTtftMs", "lastStopReason", "historyReductions",
        "thermalThrottleEvents", "thermalAbortEvents", "terminationEvents", "unloadEvents",
        "processWorkingSetBytes", "peakWorkingSetBytes", "thermalState", "thermalHeadroom",
        "isLowPowerMode", "lastMemoryPressure",
    ];

    private static (ServiceProvider Provider, IEdgeDiagnosticsContributor Contributor) Build(
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));
        extra?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var contributor = Assert.Single(
            provider.GetServices<IEdgeDiagnosticsContributor>(),
            c => c.ComponentName == "Qavren.Edge.Chat.Onnx");

        return (provider, contributor);
    }

    [Fact]
    public void EveryKeySection143ListsIsPresentByName()
    {
        var (provider, contributor) = Build();
        using (provider)
        {
            var details = contributor.Describe();

            Assert.Equal(ExpectedKeys.Order(StringComparer.Ordinal), details.Keys.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TheComponentNameContainsNoNativeSoSp1StillPicksSqliteForTheNativeBlock()
    {
        var (provider, contributor) = Build();
        using (provider)
        {
            Assert.Equal("Qavren.Edge.Chat.Onnx", contributor.ComponentName);
            Assert.DoesNotContain("Native", contributor.ComponentName, StringComparison.Ordinal);
            Assert.NotNull(contributor.ComponentVersion);
        }
    }

    [Fact]
    public void GenAiVersionReadsThePinBecauseTheAssemblyCannotAnswer()
    {
        // Plan adjustment 1: the GenAI managed assembly reports AssemblyVersion 0.0.0.0,
        // FileVersion 0.0.0.0 and carries no AssemblyInformationalVersionAttribute, and its managed
        // surface has no version API. The value comes from the CPM pin through assembly metadata,
        // so a bump that forgets the README is loud.
        var (provider, contributor) = Build();
        using (provider)
        {
            Assert.Equal("0.15.2", contributor.Describe()["genAiVersion"]);
        }
    }

    [Fact]
    public void GenAiManagedAssetReportsTheNet80MvidOnTheHostLeg()
    {
        // Plan adjustment 2: none of the five lib/ assets carries a TargetFrameworkAttribute, and
        // the iOS and Mac Catalyst assets have identical referenced-assembly sets - so the key is an
        // MVID plus a reference signature, and only the MVID separates those two.
        var (provider, contributor) = Build();
        using (provider)
        {
            var asset = contributor.Describe()["genAiManagedAsset"];

            Assert.StartsWith("324d5b97-7f06-44d8-b891-c7c016bf320a", asset!, StringComparison.Ordinal);
            Assert.Contains("System.Runtime/8.0.0.0", asset, StringComparison.Ordinal);
            Assert.DoesNotContain("android", asset, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChatIntraOpNumThreadsIsNeverZeroAndNeverEmptyWhenNothingIsDeclared()
    {
        // Plan adjustment 29. 0 is ORT's "pick for me" sentinel and would read as a real answer.
        var (provider, contributor) = Build();
        using (provider)
        {
            Assert.Equal("(not declared)", contributor.Describe()["chatIntraOpNumThreads"]);
        }
    }

    [Fact]
    public void ThePresetAndBudgetConstantsAreReportedBeforeAnythingIsLoaded()
    {
        var (provider, contributor) = Build();
        using (provider)
        {
            var details = contributor.Describe();

            Assert.Equal("llama-3.2-1b-instruct-int4", details["presetId"]);
            Assert.Equal("LicenseRef-LLAMA-3.2-Community", details["spdxLicense"]);
            Assert.Equal("1241451982", details["bundleBytes"]);
            Assert.Equal("1224236346", details["weightsBytes"]);
            Assert.Equal("32768", details["kvBytesPerToken"]);
            Assert.Equal("2", details["kvBytesPerElement"]);
            Assert.Equal("4096", details["requestedContextTokens"]);
            Assert.Equal("201326592", details["workspaceBytes"]);
            Assert.Equal("201326592", details["reserveBytes"]);
            Assert.Equal("Unprobed", details["guidanceEnforced"]);
            Assert.Equal("False", details["loaded"]);

            // Nothing is loaded, so the model-side reads are null rather than a fabricated zero.
            Assert.Null(details["resolvedContextTokens"]);
            Assert.Null(details["budgetVerdict"]);
            Assert.Null(details["turns"]);
        }
    }

    [Fact]
    public void AReadThatThrowsRendersAsTheUnavailableSentinelAndAReportNeverThrows()
    {
        // The realistic frame: the RESOLUTION throws, not a property on an already-resolved service.
        // Every value taken through that resolution becomes the sentinel - a null would say "not
        // registered", which is a different and false statement.
        var (provider, contributor) = Build(services =>
            services.AddSingleton<ChatModelHost>(_ => throw new BadImageFormatException("the host factory fails here")));

        using (provider)
        {
            var details = contributor.Describe();

            Assert.Equal("(unavailable: BadImageFormatException)", details["chatIntraOpNumThreads"]);
            Assert.Equal("(unavailable: BadImageFormatException)", details["guidanceEnforced"]);
            Assert.Equal("(unavailable: BadImageFormatException)", details["loaded"]);
            Assert.Equal("(unavailable: BadImageFormatException)", details["budgetExplanation"]);

            // The sentinel is per-read, not per-block: a preset constant is still a real value.
            Assert.Equal("llama-3.2-1b-instruct-int4", details["presetId"]);
        }
    }

    [Fact]
    public void KeyedRegistrationsAreToldApartByServiceKeyAndNotByADecoratedComponentName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4)
            .AddOnnxChat("small", ChatPresets.Qwen3_600MInt4));

        using var provider = services.BuildServiceProvider();

        var contributors = provider.GetServices<IEdgeDiagnosticsContributor>()
            .Where(c => c.ComponentName == "Qavren.Edge.Chat.Onnx")
            .ToList();

        Assert.Equal(2, contributors.Count);

        var keys = contributors.Select(c => c.Describe()["serviceKey"]).ToList();
        Assert.Contains(keys, k => k is null);
        Assert.Contains(keys, k => k == "small");

        // Both keep the same component name, so sub-project 1's Native-block rule still holds.
        Assert.All(contributors, c => Assert.Equal("Qavren.Edge.Chat.Onnx", c.ComponentName));
    }
}
