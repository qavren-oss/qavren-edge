using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 9.2's allowlist, as a table over the RID and ABI set measured from the shipped
/// <c>Microsoft.ML.OnnxRuntimeGenAI</c> 0.15.2 package.
/// </summary>
public class RuntimeSupportTests
{
    [Theory]
    [InlineData("win-x64", null)]
    [InlineData("win-arm64", null)]
    [InlineData("linux-x64", null)]
    [InlineData("linux-arm64", null)]
    [InlineData("osx-arm64", null)]
    [InlineData("ios-arm64", null)]
    [InlineData("iossimulator-arm64", null)]
    [InlineData("maccatalyst-arm64", null)]
    [InlineData("android-arm64", "arm64-v8a")]
    [InlineData("android-x64", "x86_64")]
    public void EveryRuntimeTheNativePayloadCoversIsSupported(string runtimeIdentifier, string? abi)
        => Assert.True(GenAiRuntimeSupport.IsSupported(runtimeIdentifier, abi));

    [Theory]
    [InlineData("osx-x64", null)]
    [InlineData("win-x86", null)]
    [InlineData("linux-musl-x64", null)]
    public void ARuntimeWithNoNativeIsRefused(string runtimeIdentifier, string? abi)
        => Assert.False(GenAiRuntimeSupport.IsSupported(runtimeIdentifier, abi));

    [Fact]
    public void ArmeabiV7aIs7004NamingTheAbiAndTheTwoThatWork()
    {
        // The AAR's jni/ holds arm64-v8a and x86_64 only. SP1's SQLite native DOES ship android-arm,
        // so this device gets the database and the embeddings and has no chat at all - and the
        // message has to say that rather than leaving it as a DllNotFoundException on the first
        // message.
        Assert.False(GenAiRuntimeSupport.IsSupported("android-arm", "armeabi-v7a"));

        var exception = Assert.Throws<EdgeChatException>(
            () => GenAiRuntimeSupport.ThrowIfUnsupported(
                StubDeviceProfile.Android(StubDeviceProfile.Android8Gb) with
                {
                    RuntimeIdentifier = "android-arm",
                    Abi = "armeabi-v7a",
                }));

        Assert.Equal(EdgeErrorCode.ChatUnsupportedRuntime, exception.Code);
        Assert.Contains("armeabi-v7a", exception.Message, StringComparison.Ordinal);
        Assert.Contains("arm64-v8a", exception.Message, StringComparison.Ordinal);
        Assert.Contains("x86_64", exception.Message, StringComparison.Ordinal);
        Assert.Contains("android-arm", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OsxX64Is7004NamingTheRidAndListingTheOnesThatWork()
    {
        var exception = Assert.Throws<EdgeChatException>(
            () => GenAiRuntimeSupport.ThrowIfUnsupported(
                StubDeviceProfile.Desktop(16L * 1024 * 1024 * 1024) with { RuntimeIdentifier = "osx-x64" }));

        Assert.Equal(EdgeErrorCode.ChatUnsupportedRuntime, exception.Code);
        Assert.Equal("osx-x64", exception.RuntimeIdentifier);
        Assert.Contains("osx-x64", exception.Message, StringComparison.Ordinal);
        Assert.Contains("osx-arm64", exception.Message, StringComparison.Ordinal);
        Assert.Contains("win-x64", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.Remediation);
    }

    [Fact]
    public void ASupportedRuntimeThrowsNothing()
        => GenAiRuntimeSupport.ThrowIfUnsupported(StubDeviceProfile.Desktop(16L * 1024 * 1024 * 1024));

    [Fact]
    public void TheAllowlistIsTheMeasuredSetAndNotAGuess()
    {
        // No osx-x64, no win-x86 - the package carries neither.
        Assert.Equal(
            ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64"],
            GenAiRuntimeSupport.SupportedDesktopRuntimeIdentifiers);

        // No armeabi-v7a - the AAR's jni/ does not have it.
        Assert.Equal(["arm64-v8a", "x86_64"], GenAiRuntimeSupport.SupportedAndroidAbis);
    }
}
