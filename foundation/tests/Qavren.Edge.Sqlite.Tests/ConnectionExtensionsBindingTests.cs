using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class ConnectionExtensionsBindingTests
{
    private static async Task<(EdgeTestHost Host, SqliteConnection Connection)> OpenAsync(CancellationToken ct)
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct).ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "CREATE TABLE b(id INTEGER PRIMARY KEY, name TEXT, score REAL, flag INTEGER, payload BLOB)",
            cancellationToken: ct).ConfigureAwait(false);
        return (host, connection);
    }

    [Fact]
    public async Task AnonymousObject_BindsEveryPublicProperty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);

        var rows = await connection.ExecuteAsync(
            "INSERT INTO b(id, name, score, flag) VALUES ($Id, $Name, $Score, $Flag)",
            SqliteConnectionExtensions.ToParameters(new { Id = 1L, Name = "alpha", Score = 2.5, Flag = true }),
            ct);

        Assert.Equal(1, rows);
        Assert.Equal("alpha", await connection.ScalarAsync<string>("SELECT name FROM b WHERE id = 1", cancellationToken: ct));
        Assert.Equal(2.5, await connection.ScalarAsync<double>("SELECT score FROM b WHERE id = 1", cancellationToken: ct));
        Assert.Equal(1L, await connection.ScalarAsync<long>("SELECT flag FROM b WHERE id = 1", cancellationToken: ct));
    }

    [Fact]
    public async Task NullProperty_BindsAsDbNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);

        await connection.ExecuteAsync(
            "INSERT INTO b(id, name) VALUES ($Id, $Name)",
            SqliteConnectionExtensions.ToParameters(new { Id = 2L, Name = (string?)null }),
            ct);

        Assert.Equal(1L, await connection.ScalarAsync<long>(
            "SELECT count(*) FROM b WHERE id = 2 AND name IS NULL", cancellationToken: ct));
    }

    [Theory]
    [InlineData(true)]   // ReadOnlyMemory<float>
    [InlineData(false)]  // float[]
    public async Task FloatVector_BindsAsAVecBlob(bool asMemory)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);

        float[] vector = [1.0f, -2.5f, 3.25f, 0.0f];

        // ReadOnlyMemory<float>, not Memory<float>: the plan writes `vector.AsMemory()`, which is
        // typed Memory<float> and is NOT one of the two shapes ToParameters converts, so it reaches
        // Microsoft.Data.Sqlite as an unmappable CLR type. The declared local pins the documented
        // spec 8.6 shape. See the note on this task about widening ToParameters.
        ReadOnlyMemory<float> memory = vector;
        object parameters = asMemory
            ? new { Id = 3L, Payload = memory }
            : new { Id = 3L, Payload = vector };

        await connection.ExecuteAsync(
            "INSERT INTO b(id, payload) VALUES ($Id, $Payload)",
            SqliteConnectionExtensions.ToParameters(parameters),
            ct);

        var stored = await connection.QueryAsync(
            "SELECT payload FROM b WHERE id = 3",
            reader => (byte[])reader["payload"],
            cancellationToken: ct);

        Assert.Single(stored);
        Assert.Equal(vector.Length * sizeof(float), stored[0].Length);
        Assert.Equal(vector, VecBlob.ToFloats(stored[0]));

        // And the native side agrees it is a vector, not just a blob of the right size.
        Assert.Equal(4L, await connection.ScalarAsync<long>(
            "SELECT vec_length(payload) FROM b WHERE id = 3", cancellationToken: ct));
    }

    [Fact]
    public async Task ExplicitSqliteParameters_BindByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);

        SqliteParameter[] insert = [new SqliteParameter("$id", 4L), new SqliteParameter("$name", "beta")];
        await connection.ExecuteAsync(
            "INSERT INTO b(id, name) VALUES ($id, $name)",
            insert,
            ct);

        SqliteParameter[] lookup = [new SqliteParameter("$id", 4L)];
        var names = await connection.QueryAsync(
            "SELECT name FROM b WHERE id = $id",
            reader => reader.GetString(0),
            lookup,
            ct);

        var name = Assert.Single(names);
        Assert.Equal("beta", name);
    }

    [Fact]
    public async Task ScalarAsync_ReturnsDefaultForNoRowAndForNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);

        Assert.Null(await connection.ScalarAsync<string>("SELECT name FROM b WHERE id = 999", cancellationToken: ct));
        Assert.Equal(0L, await connection.ScalarAsync<long>("SELECT NULL", cancellationToken: ct));
    }
}
