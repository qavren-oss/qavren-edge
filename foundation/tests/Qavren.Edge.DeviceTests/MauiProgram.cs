using DeviceRunners.VisualRunners;

namespace Qavren.Edge.DeviceTests;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseVisualTestRunner(config => config
                .AddCliConfiguration()          // reads config from `dotnet test` / the DeviceRunners CLI
                .AddConsoleResultChannel()
                // Spec 13: "hosting the same test assemblies". All three, in one runner.
                .AddTestAssembly(typeof(MauiProgram).Assembly)
                .AddTestAssemblies(
                    typeof(Qavren.Edge.Core.Tests.PathsTests).Assembly,
                    typeof(Qavren.Edge.Sqlite.Tests.MigrationTests).Assembly)
                .AddXunit3());

        return builder.Build();
    }
}
