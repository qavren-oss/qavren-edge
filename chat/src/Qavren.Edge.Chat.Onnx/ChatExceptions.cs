using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

/// <summary>
/// Every chat failure that is not a provisioning failure: the environment task, the model host,
/// the memory budget, the shape reader and the decode loop all raise this one type.
/// </summary>
/// <remarks>
/// Derives from sub-project 1's <see cref="EdgeException"/>, so the <c>HelpLink</c> convention
/// (<c>foundation/docs/errors.md#&lt;code&gt;</c>) applies for free. Provisioning failures reuse
/// sub-project 2's <c>EdgeModelProvisioningException</c> and its 5053-5056 codes rather than
/// minting duplicates (spec 15.2); 7051, 7052 and 7053 are the three cases sub-project 2 has no
/// code for and they are raised from here.
/// </remarks>
public sealed class EdgeChatException : EdgeException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The error code; the help link is derived from it.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, when there is one.</param>
    public EdgeChatException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }

    /// <summary>The preset this failure concerns, when it concerns one.</summary>
    public string? PresetId { get; init; }

    /// <summary>The model id this failure concerns, when it concerns one.</summary>
    public string? ModelId { get; init; }

    /// <summary>The bytes the operation needed, when a memory or disk budget was the cause.</summary>
    public long? RequiredBytes { get; init; }

    /// <summary>The bytes the OS reported available.</summary>
    public long? AvailableBytes { get; init; }

    /// <summary>The device's total memory, as <see cref="EdgeChatDeviceProfile"/> read it.</summary>
    public long? TotalMemoryBytes { get; init; }

    /// <summary>How to read <see cref="AvailableBytes"/>.</summary>
    public EdgeMemoryBudgetKind? BudgetKind { get; init; }

    /// <summary>The context length that was asked for.</summary>
    public int? RequestedContextTokens { get; init; }

    /// <summary>The largest context that would have fit, when any would have.</summary>
    public int? FittingContextTokens { get; init; }

    /// <summary>The prompt length, when the prompt was the cause.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>The thermal state, when thermal pressure was the cause.</summary>
    public EdgeThermalState? Thermal { get; init; }

    /// <summary>The host runtime identifier.</summary>
    public string? RuntimeIdentifier { get; init; }

    /// <summary>What the caller can do about it, in one sentence.</summary>
    public string? Remediation { get; init; }
}
