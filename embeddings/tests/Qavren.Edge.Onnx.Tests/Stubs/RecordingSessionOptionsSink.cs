using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx.Tests.Stubs;

/// <summary>
/// Records every call the execution-provider policy and the session-options factory would make to
/// ORT. This is what lets spec 16.1's session-factory assertions run with no session, no
/// <c>OrtEnv</c> and no native library - and it is the only way to assert "CoreML was appended
/// with these options" from a Windows host, where a real CoreML append throws.
/// </summary>
internal sealed class RecordingSessionOptionsSink : ISessionOptionsSink
{
    private readonly Func<string, Exception?>? _failFor;

    public RecordingSessionOptionsSink(Func<string, Exception?>? failFor = null) => _failFor = failFor;

    public List<(string Provider, Dictionary<string, string> Options)> Appended { get; } = [];

    public List<(string Name, long Value)> FreeDimensionOverrides { get; } = [];

    public List<(string Key, string Value)> SessionConfigEntries { get; } = [];

    public int DisablePerSessionThreadsCount { get; private set; }

    public int IntraOpNumThreads { get; set; }

    public GraphOptimizationLevel GraphOptimizationLevel { get; set; }

    public void AppendExecutionProvider(string providerName, Dictionary<string, string> providerOptions)
    {
        var failure = _failFor?.Invoke(providerName);
        if (failure is not null)
        {
            throw failure;
        }

        Appended.Add((providerName, providerOptions));
    }

    public void AddFreeDimensionOverrideByName(string dimensionName, long dimensionValue)
        => FreeDimensionOverrides.Add((dimensionName, dimensionValue));

    public void AddSessionConfigEntry(string configKey, string configValue)
        => SessionConfigEntries.Add((configKey, configValue));

    public void DisablePerSessionThreads() => DisablePerSessionThreadsCount++;
}
