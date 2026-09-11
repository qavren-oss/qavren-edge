using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sample.Pages;

public partial class DiagnosticsPage : ContentPage
{
    private readonly IEdgeDiagnostics _diagnostics;
    private bool _loadingToggles;

    public DiagnosticsPage(IEdgeDiagnostics diagnostics)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Reflects what THIS launch was built with, not what a not-yet-applied toggle will do -
        // hence loading them without re-triggering the "restart to apply" hint.
        _loadingToggles = true;
        ProfileComputePlanSwitch.IsToggled =
            Preferences.Default.Get(MauiProgram.ProfileComputePlanPreferenceKey, false);
        IncludeRowCountsSwitch.IsToggled =
            Preferences.Default.Get(MauiProgram.IncludeRowCountsPreferenceKey, false);
        _loadingToggles = false;

        // The report renderer walks every registered IEdgeDiagnosticsContributor generically (spec
        // 18: "no code change") - the Onnx, Embeddings and VectorData components from Task 7.2's
        // wiring show up here with nothing added below.
        ReportLabel.Text = EdgeDiagnosticsRenderer.ToText(_diagnostics.Report());
    }

    private void OnProfileComputePlanToggled(object? sender, ToggledEventArgs e)
    {
        if (_loadingToggles)
        {
            return;
        }

        Preferences.Default.Set(MauiProgram.ProfileComputePlanPreferenceKey, e.Value);
        ShowRestartHint();
    }

    private void OnIncludeRowCountsToggled(object? sender, ToggledEventArgs e)
    {
        if (_loadingToggles)
        {
            return;
        }

        Preferences.Default.Set(MauiProgram.IncludeRowCountsPreferenceKey, e.Value);
        ShowRestartHint();
    }

    private void ShowRestartHint() =>
        RestartHintLabel.Text = "Restart the app to apply - both options are read once at startup.";
}
