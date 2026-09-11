using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Sample.Migrations;

public sealed class M001_CreateNotes : IEdgeMigration
{
    public int Version => 1;

    public string Name => "create notes";

    public async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL)",
            cancellationToken: cancellationToken);

        await VecTable.CreateAsync(connection, "notes_vec", dims: 384, cancellationToken: cancellationToken);

        await FtsTable.CreateAsync(
            connection, "notes_fts", ["title", "body"], contentTable: "notes",
            cancellationToken: cancellationToken);

        await FtsTable.CreateSyncTriggersAsync(
            connection, "notes_fts", "notes", ["title", "body"], cancellationToken);
    }
}
