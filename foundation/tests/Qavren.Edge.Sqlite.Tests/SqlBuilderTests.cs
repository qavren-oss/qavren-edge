using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class SqlBuilderTests
{
    [Fact]
    public void VecTable_DefaultShape()
        => Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_vec\" USING vec0(embedding float[384] distance_metric=cosine)",
            VecTable.BuildCreateSql("notes_vec", dims: 384));

    [Fact]
    public void VecTable_WithAuxiliaryMetadataAndPartitionColumns()
    {
        var sql = VecTable.BuildCreateSql(
            "notes_vec",
            dims: 4,
            metric: VecMetric.L2,
            elementType: VecElementType.Float32,
            aux: [new VecAuxColumn("contents", "TEXT")],
            metadata: [new VecMetadataColumn("label", "TEXT")],
            partitions: [new VecPartitionKey("tenant_id", "INTEGER")],
            chunkSize: 1024);

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_vec\" USING vec0(" +
            "embedding float[4] distance_metric=l2, " +
            "tenant_id INTEGER partition key, " +
            "label TEXT, " +
            "+contents TEXT, " +
            "chunk_size=1024)",
            sql);
    }

    [Fact]
    public void VecTable_BitColumnsRejectDistanceMetric()
        => Assert.Throws<ArgumentException>(() =>
            VecTable.BuildCreateSql("t", dims: 8, metric: VecMetric.Cosine, elementType: VecElementType.Bit));

    [Fact]
    public void VecTable_BitColumnsAreAllowedWithoutAMetric()
        => Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"t\" USING vec0(embedding bit[8])",
            VecTable.BuildCreateSql("t", dims: 8, metric: null, elementType: VecElementType.Bit));

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    public void VecTable_RejectsOutOfRangeDimensions(int dims)
        => Assert.Throws<ArgumentOutOfRangeException>(() => VecTable.BuildCreateSql("t", dims));

    [Theory]
    [InlineData(0)]
    [InlineData(12)]     // not a multiple of 8
    [InlineData(8192)]   // above SQLITE_VEC_CHUNK_SIZE_MAX (4096)
    public void VecTable_RejectsInvalidChunkSize(int chunkSize)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => VecTable.BuildCreateSql("t", dims: 4, chunkSize: chunkSize));

    [Fact]
    public void Knn_BuildsMatchAndKWithOptionalFilter()
    {
        Assert.Equal(
            "SELECT rowid, distance FROM \"notes_vec\" WHERE embedding MATCH $query AND k = $k",
            Knn.BuildSql("notes_vec"));

        Assert.Equal(
            "SELECT rowid, distance FROM \"notes_vec\" WHERE embedding MATCH $query AND k = $k AND label = $label",
            Knn.BuildSql("notes_vec", where: "label = $label"));
    }

    [Fact]
    public void FtsTable_StandaloneAndExternalContent()
    {
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(title, body, tokenize='unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["title", "body"]));

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(title, body, content='notes', tokenize='porter unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["title", "body"], FtsTokenizer.Porter, contentTable: "notes"));
    }

    [Fact]
    public void FtsTable_SyncTriggersCoverInsertUpdateDelete()
    {
        var statements = FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["title", "body"]);

        Assert.Equal(3, statements.Count);
        Assert.Contains(statements, s => s.Contains("AFTER INSERT ON \"notes\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("AFTER DELETE ON \"notes\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("AFTER UPDATE ON \"notes\"", StringComparison.Ordinal));
        Assert.All(statements, s => Assert.Contains("notes_fts", s, StringComparison.Ordinal));
    }
}
