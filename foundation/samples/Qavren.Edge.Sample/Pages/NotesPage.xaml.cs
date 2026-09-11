using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Sample.Pages;

public partial class NotesPage : ContentPage
{
    private readonly IEdgeDatabase _database;
    private readonly Random _random = new();

    public NotesPage(IEdgeDatabase database)
    {
        InitializeComponent();
        _database = database;
    }

    private float[] RandomVector()
    {
        var v = new float[384];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(_random.NextDouble() * 2.0 - 1.0);
        }

        return v;
    }

    private async void OnAdd(object? sender, EventArgs e)
    {
        var title = string.IsNullOrWhiteSpace(TitleEntry.Text) ? "note" : TitleEntry.Text;

        await _database.ExecuteInTransactionAsync(async (connection, transaction, ct) =>
        {
            await connection.ExecuteAsync(
                "INSERT INTO notes(title, body) VALUES ($title, $body)",
                [new SqliteParameter("$title", title), new SqliteParameter("$body", "sample body")],
                ct);

            var id = await connection.ScalarAsync<long>("SELECT last_insert_rowid()", cancellationToken: ct);

            await connection.ExecuteAsync(
                "INSERT INTO notes_vec(rowid, embedding) VALUES ($id, $e)",
                [new SqliteParameter("$id", id), new SqliteParameter("$e", VecBlob.From(RandomVector()))],
                ct);

            return id;
        });

        await DisplayAlertAsync("Added", $"Inserted '{title}' with a random 384-d vector.", "OK");
    }

    private async void OnSearch(object? sender, EventArgs e)
    {
        await using var connection = await _database.OpenConnectionAsync();
        var hits = await Knn.QueryAsync(connection, "notes_vec", RandomVector().AsMemory(), k: 10);
        Results.ItemsSource = hits.Select(h => $"rowid {h.RowId}  distance {h.Distance:F4}").ToArray();
    }
}
