using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier3;

/// <summary>
/// The <c>SkipUnless</c> gate for tier 3. Evaluated at RUNTIME, after the class constructor and
/// after every <c>BeforeAfterTestAttribute</c> - which is exactly why nothing in a tier-3 class's
/// constructor may touch a model: a "skipped" fact that loaded 495 MB first would cost every
/// device lane and every PR leg the download this tier exists to keep off them. Sub-project 2's
/// <c>ModelAvailable</c> pattern, copied.
/// </summary>
/// <remarks>
/// This property lives on its own type so every fact names it with
/// <c>SkipType = typeof(ChatModelAvailable)</c>. xunit resolves <c>SkipUnless</c> against the test
/// class unless <c>SkipType</c> says otherwise, and a bare property name would send it looking for
/// <c>RealModelFacts.Yes</c> and fail the test rather than skip it.
/// </remarks>
public static class ChatModelAvailable
{
    /// <summary>The environment variable ci.yml's nightly <c>chat-model-tests</c> job sets.</summary>
    public const string Variable = "QAVREN_EDGE_CHAT_MODEL_DIR";

    /// <summary>The skip reason every fact carries.</summary>
    public const string SkipReason = Variable + " not set";

    /// <summary>
    /// Set only by ci.yml's nightly job, and by a developer opting in. The gate is the variable
    /// ALONE (plan and spec section 16.3): a set variable pointing at a mis-staged directory must
    /// surface as 7051/7007 inside the fact body, loudly - not as a skip that turns the lane green.
    /// </summary>
    public static bool Yes => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };

    /// <summary>The staged model directory this process was pointed at.</summary>
    public static string StagedDirectory =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } dir
            ? dir
            : throw new InvalidOperationException(
                Variable + " is not set. Tier 3 runs only with a staged model; every other lane skips it.");
}

/// <summary>
/// The tier-3 collection: no fixture (every fact builds its own container inside its body, so a
/// skipped fact loads nothing), but a named collection so the facts run serially and after every
/// other collection - real natives, one process, the same reason tier 2 is ordered last.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RealModelCollectionDefinition
{
    /// <summary>Sorts after tier 2's display name, so the real model follows the fixture.</summary>
    public const string Name = NativesLastCollectionOrderer.NativesPrefix + " tier 3 (real model)";
}
