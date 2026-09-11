using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sample.Pages;

public partial class DiagnosticsPage : ContentPage
{
    private readonly IEdgeDiagnostics _diagnostics;

    public DiagnosticsPage(IEdgeDiagnostics diagnostics)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReportLabel.Text = EdgeDiagnosticsRenderer.ToText(_diagnostics.Report());
    }
}
