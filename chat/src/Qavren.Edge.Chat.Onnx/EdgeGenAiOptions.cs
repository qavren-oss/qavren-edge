namespace Qavren.Edge.Chat;

/// <summary>
/// The three switches on the process-wide ORT GenAI runtime. Read once, by the order-400
/// <c>ChatEnvironmentStartupTask</c>, and by nothing else.
/// </summary>
/// <remarks>
/// Separate from <see cref="EdgeChatOptions"/> on purpose: everything here is <b>per process</b>,
/// and a keyed second <c>AddOnnxChat</c> registration gets its own <see cref="EdgeChatOptions"/>
/// but shares this one. A per-registration copy would let two registrations disagree about whether
/// telemetry is off, which is not a state the native library can be in.
/// </remarks>
public sealed class EdgeGenAiOptions
{
    /// <summary>
    /// Create and own the single process-wide <c>OgaHandle</c>. Default <see langword="true"/>.
    /// <para>
    /// Set it false only in a process where something else already owns the handle - a host app
    /// that constructed one before Qavren.Edge was composed. Sub-project 4 then touches neither its
    /// creation nor its disposal, and <see cref="ShutdownOnStopping"/> becomes inert.
    /// </para>
    /// </summary>
    public bool OwnRuntimeHandle { get; set; } = true;

    /// <summary>
    /// Call <c>Utils.DisableTelemetryEvents()</c> at order 400. <b>Default <see langword="true"/>,
    /// and the default is the whole point.</b>
    /// <para>
    /// ORT GenAI 0.15.0 made its 1DS telemetry opt-<i>out</i>, and the Android AAR merges
    /// <c>INTERNET</c>, <c>ACCESS_NETWORK_STATE</c> and an
    /// <c>ai.onnxruntime.genai.TelemetryInitializer</c> content provider into every consuming APK -
    /// all four measured from the shipped 0.15.2 package, not inferred. An on-device suite whose
    /// selling point is that nothing leaves the device does not ship with that on by default.
    /// </para>
    /// </summary>
    public bool DisableTelemetry { get; set; } = true;

    /// <summary>
    /// Dispose the <c>OgaHandle</c> - which calls <c>OgaShutdown()</c> - when sub-project 1's
    /// lifecycle hub raises <c>Stopping</c>. Default <see langword="true"/>.
    /// <para>
    /// Safe since GenAI 0.15.0, which re-initialises transparently on the next call, and
    /// <b>proved on this box</b> rather than trusted: a handle was created, disposed, and a second
    /// one created afterwards without error. ORT's own <c>OrtEnv</c> is <b>not</b> disposed - it is
    /// a process-wide singleton with a one-shot options hook, and tearing it down would silently
    /// break a second Edge host in the same process.
    /// </para>
    /// </summary>
    public bool ShutdownOnStopping { get; set; } = true;
}
