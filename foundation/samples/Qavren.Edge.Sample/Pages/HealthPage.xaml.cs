using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Sample.Pages;

public partial class HealthPage : ContentPage
{
    private readonly IEdgeDatabase _database;

    public HealthPage(IEdgeDatabase database)
    {
        InitializeComponent();
        _database = database;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var info = await _database.GetInfoAsync();
        InfoLabel.Text =
            $"sqlite {info.SqliteVersion}\nvec {info.VecVersion}\npage_size {info.PageSize}\n" +
            $"journal {info.JournalMode}\nuser_version {info.UserVersion}\n" +
            $"size {info.FileSizeBytes} bytes\nencrypted {info.IsEncrypted}";

        var check = await _database.CheckAsync();
        CheckLabel.Text = $"quick_check: {check.QuickCheck}  vec: {check.VecVersion}";
    }

    private async void OnCheckpoint(object? sender, EventArgs e)
    {
        await _database.CheckpointAsync();
        CheckpointLabel.Text = $"checkpointed at {DateTimeOffset.Now:HH:mm:ss}";
    }
}
