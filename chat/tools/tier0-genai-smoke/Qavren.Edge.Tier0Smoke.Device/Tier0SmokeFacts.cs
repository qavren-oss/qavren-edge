using Xunit;

namespace Qavren.Edge.Tier0Smoke.Device;

/// <summary>The device head's one test: run the exact same shared body the console leg runs.</summary>
public class Tier0SmokeFacts
{
    [Fact]
    public async Task LoadsAndGeneratesOneToken()
    {
        var output = new StringWriter();

        var exitCode = await Tier0Smoke.RunAsync(output);

        Assert.True(exitCode == 0, $"tier-0 device leg failed:\n{output}");
    }
}
