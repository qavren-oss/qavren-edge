using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class SqliteKeyTests
{
    [Fact]
    public void FromRawBytes_ProducesTheSqlCipherRawKeyLiteral()
    {
        var bytes = new byte[32];
        bytes[0] = 0x2D;
        bytes[31] = 0x99;

        var key = SqliteKey.FromRawBytes(bytes);

        Assert.True(key.IsRaw);
        // 32 bytes -> 64 hex chars: "2D" + 30 zero bytes (60 chars) + "99".
        Assert.Equal("x'2D" + new string('0', 60) + "99'", key.ToConnectionStringPassword());
        Assert.Equal(67, key.ToConnectionStringPassword().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromRawBytes_RejectsAnythingButThirtyTwoBytes(int length)
        => Assert.Throws<ArgumentException>(() => SqliteKey.FromRawBytes(new byte[length]));

    [Fact]
    public void FromPassphrase_PassesTheTextThroughUnchanged()
    {
        var key = SqliteKey.FromPassphrase("correct horse battery staple");

        Assert.False(key.IsRaw);
        Assert.Equal("correct horse battery staple", key.ToConnectionStringPassword());
    }

    [Fact]
    public void FromPassphrase_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => SqliteKey.FromPassphrase("   "));

    [Fact]
    public void Options_HaveTheDocumentedDefaults()
    {
        var options = new SqliteOptions();

        Assert.Equal("edge.db", options.DatabaseName);
        Assert.Equal(SqliteJournalMode.Wal, options.JournalMode);
        Assert.Equal(SqliteSynchronousMode.Normal, options.Synchronous);
        Assert.Equal(TimeSpan.FromSeconds(5), options.BusyTimeout);
        Assert.True(options.ForeignKeys);
        Assert.True(options.Pooling);
        Assert.Equal(8192, options.CacheSizeKiB);
        Assert.Null(options.Key);
        Assert.Null(options.KeyProvider);
    }
}
