using Qavren.Edge.Sqlite.Fts;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class FtsTableOptionsTests
{
    [Fact]
    public void ExternalContentWithRowIdAndDiacritics()
    {
        var sql = FtsTable.BuildCreateSql(
            "notes_fts",
            ["Title", "Body"],
            FtsTokenizer.Unicode61,
            new FtsTableOptions { ContentTable = "notes", ContentRowId = "_rowid" });

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(" +
            "Title, Body, content='notes', content_rowid='_rowid', " +
            "tokenize='unicode61 remove_diacritics 2')",
            sql);
    }

    [Fact]
    public void AnApostropheInTheContentTableNameIsDoubledNotLeftToEndTheOption()
    {
        // content= and content_rowid= are string literals to FTS5. Left alone, the quote in the name
        // ends the option and the rest of the name is parsed as more options.
        var withOptions = FtsTable.BuildCreateSql(
            "notes_fts",
            ["Body"],
            FtsTokenizer.Unicode61,
            new FtsTableOptions { ContentTable = "john's notes", ContentRowId = "_rowid" });

        var simple = FtsTable.BuildCreateSql("notes_fts", ["Body"], FtsTokenizer.Unicode61, contentTable: "john's notes");

        Assert.Contains("content='john''s notes', content_rowid='_rowid'", withOptions, StringComparison.Ordinal);
        Assert.Contains("content='john''s notes'", simple, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefixIsEmittedWhenSet()
    {
        var sql = FtsTable.BuildCreateSql(
            "t_fts", ["Body"], FtsTokenizer.Unicode61,
            new FtsTableOptions { ContentTable = "t", ContentRowId = "_rowid", Prefix = "2 3" });

        Assert.Contains("prefix='2 3'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDiacriticsIsOmittedForTokenizersThatRejectIt()
    {
        var ascii = FtsTable.BuildCreateSql("t_fts", ["Body"], FtsTokenizer.Ascii, new FtsTableOptions());
        var porter = FtsTable.BuildCreateSql("t_fts", ["Body"], FtsTokenizer.Porter, new FtsTableOptions());

        Assert.DoesNotContain("remove_diacritics", ascii, StringComparison.Ordinal);
        Assert.DoesNotContain("remove_diacritics", porter, StringComparison.Ordinal);
        Assert.Contains("tokenize='ascii'", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggersUseTheSuppliedContentRowId()
    {
        var triggers = FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["Title", "Body"], "_rowid");

        Assert.Equal(3, triggers.Count);
        Assert.All(triggers, t => Assert.Contains("\"_rowid\"", t, StringComparison.Ordinal));
        Assert.Contains("AFTER INSERT ON \"notes\"", triggers[0], StringComparison.Ordinal);
        Assert.Contains("VALUES (new.\"_rowid\", new.Title, new.Body)", triggers[0], StringComparison.Ordinal);
        Assert.Contains("'delete', old.\"_rowid\"", triggers[1], StringComparison.Ordinal);
        Assert.Contains("'delete', old.\"_rowid\"", triggers[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFourArgumentOverloadIsUnchanged()
    {
        // SP1's merged behaviour, asserted here as well so an edit to the shared emitter that
        // breaks it fails in SP2's own test file too, not only in SP1's.
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(" +
            "Title, Body, content='notes', tokenize='unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["Title", "Body"], FtsTokenizer.Unicode61, "notes"));
    }
}
