using DeviceRunners.VisualRunners;

namespace Qavren.Edge.Tier0Smoke.Device;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseVisualTestRunner(config => config
                .AddCliConfiguration()          // reads config from `dotnet test` / the DeviceRunners CLI
                .AddConsoleResultChannel()
                .AddTestAssembly(typeof(MauiProgram).Assembly)
                .AddXunit3());

        return builder.Build();
    }
}
