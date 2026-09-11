namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// The RID and Android-ABI allowlist, read off the shipped <c>Microsoft.ML.OnnxRuntimeGenAI</c>
/// 0.15.2 package rather than guessed.
/// </summary>
/// <remarks>
/// The native payload, listed from the package on disk, is <c>win-x64</c>, <c>win-arm64</c>,
/// <c>linux-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>, an iOS xcframework whose slices cover
/// device, simulator and Mac Catalyst, and an Android AAR whose <c>jni/</c> holds
/// <c>arm64-v8a</c> and <c>x86_64</c> and nothing else.
/// <para>
/// <b>There is no <c>osx-x64</c>, no <c>win-x86</c> and no <c>armeabi-v7a</c>.</b> Sub-project 1's
/// SQLite native ships <c>android-arm</c>, so an armeabi-v7a device gets the database and the
/// embeddings and has <b>no chat at all</b> - and the order-400 task says exactly that, by name,
/// rather than letting it surface as a <c>DllNotFoundException</c> on the user's first message.
/// </para>
/// </remarks>
internal static class GenAiRuntimeSupport
{
    /// <summary>The Android ABIs the AAR carries, in the order the message lists them.</summary>
    public static IReadOnlyList<string> SupportedAndroidAbis { get; } = ["arm64-v8a", "x86_64"];

    /// <summary>The desktop and server runtime identifiers the package carries natives for.</summary>
    public static IReadOnlyList<string> SupportedDesktopRuntimeIdentifiers { get; } =
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64"];

    /// <summary>
    /// Whether ORT GenAI ships a native for this device.
    /// </summary>
    /// <param name="runtimeIdentifier">The running RID.</param>
    /// <param name="abi">The running Android ABI, or null off Android.</param>
    /// <returns><see langword="true"/> when a native exists.</returns>
    public static bool IsSupported(string runtimeIdentifier, string? abi)
    {
        ArgumentNullException.ThrowIfNull(runtimeIdentifier);

        // Android is decided by the ABI and never by the RID: the AAR is one artefact whose jni/
        // folder is the only thing that says which devices it runs on.
        if (IsAndroid(runtimeIdentifier) || abi is { Length: > 0 })
        {
            return abi is { Length: > 0 } running
                && SupportedAndroidAbis.Contains(running, StringComparer.OrdinalIgnoreCase);
        }

        // One zipped xcframework covers ios-arm64, ios-arm64_x86_64-simulator and
        // ios-arm64_x86_64-maccatalyst, so every Apple RID this suite targets is inside it.
        if (IsApple(runtimeIdentifier))
        {
            return true;
        }

        foreach (var supported in SupportedDesktopRuntimeIdentifiers)
        {
            if (string.Equals(runtimeIdentifier, supported, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Throws <see cref="EdgeErrorCode.ChatUnsupportedRuntime"/> (7004) when no native exists,
    /// naming the RID or the ABI and listing the ones that do work.
    /// </summary>
    /// <param name="profile">The device profile read at order 400.</param>
    /// <exception cref="EdgeChatException">No GenAI native exists for this RID or ABI.</exception>
    public static void ThrowIfUnsupported(EdgeChatDeviceProfile profile)
    {
        var runtimeIdentifier = profile.RuntimeIdentifier ?? "(unknown)";
        if (IsSupported(runtimeIdentifier, profile.Abi))
        {
            return;
        }

        throw Unsupported(runtimeIdentifier, profile.Abi);
    }

    /// <summary>Builds the 7004 that <see cref="ThrowIfUnsupported"/> raises.</summary>
    /// <param name="runtimeIdentifier">The running RID.</param>
    /// <param name="abi">The running Android ABI, or null off Android.</param>
    /// <returns>The exception, ready to throw.</returns>
    public static EdgeChatException Unsupported(string runtimeIdentifier, string? abi)
    {
        var isAndroid = IsAndroid(runtimeIdentifier) || abi is { Length: > 0 };

        var message = isAndroid
            ? "ONNX Runtime GenAI 0.15.2 ships no native library for the Android ABI " +
              $"'{abi ?? "(unknown)"}'. The AAR carries {string.Join(" and ", SupportedAndroidAbis)} " +
              "only. Qavren.Edge.Sqlite does ship android-arm, so the database and the embeddings " +
              "work on this device and chat cannot."
            : "ONNX Runtime GenAI 0.15.2 ships no native library for the runtime identifier " +
              $"'{runtimeIdentifier}'. It carries " +
              $"{string.Join(", ", SupportedDesktopRuntimeIdentifiers)}, an iOS/Mac Catalyst " +
              $"xcframework, and an Android AAR for {string.Join(" and ", SupportedAndroidAbis)}.";

        return new EdgeChatException(EdgeErrorCode.ChatUnsupportedRuntime, message)
        {
            RuntimeIdentifier = runtimeIdentifier,
            Remediation = isAndroid
                ? "Ship chat to arm64-v8a and x86_64 only, and hide the chat surface on other ABIs - " +
                  "IChatModelProvisioner.Plan() and this code are both answerable before anything is downloaded."
                : "Run on a runtime identifier ORT GenAI publishes a native for, or hide the chat " +
                  "surface on this one; there is no managed fallback.",
        };
    }

    private static bool IsAndroid(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("android", StringComparison.OrdinalIgnoreCase);

    private static bool IsApple(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("ios", StringComparison.OrdinalIgnoreCase)
        || runtimeIdentifier.StartsWith("maccatalyst", StringComparison.OrdinalIgnoreCase);
}
