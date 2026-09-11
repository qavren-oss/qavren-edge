using System.Linq.Expressions;
using Qavren.Edge.VectorData.Internal;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Every supported node's emitted SQL, and every rejected node's message. The output is a predicate
/// over the DATA table, which the caller pushes into vec0 as
/// <c>rowid IN (SELECT "_rowid" FROM ... WHERE &lt;this&gt;)</c> - a true pre-filter, never a
/// client-side pass.
/// </summary>
public class FilterTranslatorTests
{
    private static EdgeFilterTranslation Translate(Expression<Func<FilterRecord, bool>> filter) =>
        new EdgeFilterTranslator().Translate(filter, ModelFactory.ModelFor<FilterRecord>("filters"));

    private static string Sql(Expression<Func<FilterRecord, bool>> filter) => Translate(filter).Sql;

    [Fact]
    public void EqualityAgainstAnInlineConstantIsInlined() =>
        Assert.Equal("\"Tag\" = 'red'", Sql(r => r.Tag == "red"));

    [Fact]
    public void AStringLiteralHasItsQuotesDoubled() =>
        Assert.Equal("\"Tag\" = 'O''Brien'", Sql(r => r.Tag == "O'Brien"));

    [Fact]
    public void ACapturedValueBecomesAParameter()
    {
        var tag = "red";
        var translation = Translate(r => r.Tag == tag);

        Assert.Equal("\"Tag\" = @p1", translation.Sql);
        var parameter = Assert.Single(translation.Parameters);
        Assert.Equal("@p1", parameter.Name);
        Assert.Equal("red", parameter.Value);
    }

    [Fact]
    public void Inequality() => Assert.Equal("\"Count\" <> 3", Sql(r => r.Count != 3));

    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void TheFourOrderingComparisons(string op)
    {
        var sql = op switch
        {
            "<" => Sql(r => r.Count < 3),
            "<=" => Sql(r => r.Count <= 3),
            ">" => Sql(r => r.Count > 3),
            _ => Sql(r => r.Count >= 3),
        };

        Assert.Equal($"\"Count\" {op} 3", sql);
    }

    [Fact]
    public void AndAlso() =>
        Assert.Equal(
            "(\"Tag\" = 'red' AND \"Count\" > 3)",
            Sql(r => r.Tag == "red" && r.Count > 3));

    [Fact]
    public void OrElse() =>
        Assert.Equal(
            "(\"Tag\" = 'red' OR \"Tag\" = 'blue')",
            Sql(r => r.Tag == "red" || r.Tag == "blue"));

    [Fact]
    public void NestedLogicKeepsItsParentheses() =>
        Assert.Equal(
            "((\"Tag\" = 'red' OR \"Tag\" = 'blue') AND \"Count\" > 3)",
            Sql(r => (r.Tag == "red" || r.Tag == "blue") && r.Count > 3));

    [Fact]
    public void NotCollapsesTheUnknownBeforeNegating()
    {
        // NOT in SQL is three-valued and NOT in C# is not. Over a row whose Tag is NULL,
        // "Tag" = 'red' is unknown, so a bare NOT (...) is unknown too and the row is dropped -
        // where !(r.Tag == "red") is true in C# and keeps it. COALESCE collapses the unknown to
        // false first, which is the C# semantics MEVD's filter contract is written in and what the
        // conformance suite's Not_over_Or and Not_over_bool assert.
        Assert.Equal("NOT COALESCE(\"Tag\" = 'red', 0)", Sql(r => !(r.Tag == "red")));
    }

    [Fact]
    public void InequalityOverANullableColumnIsNullSafe() =>
        // Tag is nullable, so <> would drop the NULL rows that C#'s != keeps. IS NOT is the
        // two-valued form of the same comparison. Count, below, is not nullable and keeps <>.
        Assert.Equal("\"Tag\" IS NOT 'red'", Sql(r => r.Tag != "red"));

    [Fact]
    public void IsNull() => Assert.Equal("\"Tag\" IS NULL", Sql(r => r.Tag == null));

    [Fact]
    public void IsNotNull() => Assert.Equal("\"Tag\" IS NOT NULL", Sql(r => r.Tag != null));

    [Fact]
    public void ABareBooleanPropertyComparesAgainstOne() =>
        Assert.Equal("\"Flag\" = 1", Sql(r => r.Flag));

    [Fact]
    public void ABooleanConstantIsRenderedAsAnInteger() =>
        Assert.Equal("\"Flag\" = 0", Sql(r => r.Flag == false));

    [Fact]
    public void ContainsOverAnInlineArrayBecomesIn() =>
        Assert.Equal(
            "\"Tag\" IN ('red', 'blue')",
            Sql(r => new[] { "red", "blue" }.Contains(r.Tag)));

    [Fact]
    public void ContainsOverACapturedEnumerableBecomesInWithOneParameterPerItem()
    {
        var tags = new List<string> { "red", "blue" };
        var translation = Translate(r => tags.Contains(r.Tag!));

        Assert.Equal("\"Tag\" IN (@p1, @p2)", translation.Sql);
        Assert.Equal(["red", "blue"], translation.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void ContainsOverAnEmptyCapturedEnumerableStaysValidSql()
    {
        var tags = new List<string>();

        // IN () is a syntax error in SQLite; IN (NULL) matches nothing, which is the same result.
        Assert.Equal("\"Tag\" IN (NULL)", Sql(r => tags.Contains(r.Tag!)));
    }

    [Fact]
    public void StartsWithBecomesLike() =>
        Assert.Equal("\"Title\" LIKE 're%' ESCAPE '\\'", Sql(r => r.Title.StartsWith("re", StringComparison.Ordinal)));

    [Fact]
    public void EndsWithBecomesLike() =>
        Assert.Equal("\"Title\" LIKE '%ed' ESCAPE '\\'", Sql(r => r.Title.EndsWith("ed", StringComparison.Ordinal)));

    [Fact]
    public void ContainsOnAStringBecomesLike() =>
        Assert.Equal("\"Title\" LIKE '%ed%' ESCAPE '\\'", Sql(r => r.Title.Contains("ed", StringComparison.Ordinal)));

    [Fact]
    public void TheOverloadWithoutAStringComparisonTranslatesIdentically()
    {
        // SQLite's LIKE folds ASCII case and nothing else, whatever comparison the caller names,
        // so both overloads land on the same SQL. The conformance suite uses this one.
#pragma warning disable CA1310 // the point of the test is the overload that omits the comparison
        Assert.Equal("\"Title\" LIKE 're%' ESCAPE '\\'", Sql(r => r.Title.StartsWith("re")));
        Assert.Equal("\"Title\" LIKE '%ed' ESCAPE '\\'", Sql(r => r.Title.EndsWith("ed")));
#pragma warning restore CA1310
    }

    [Fact]
    public void LikeWildcardsInsideThePatternAreEscaped() =>
        Assert.Equal(
            "\"Title\" LIKE '100\\% of a\\_b%' ESCAPE '\\'",
            Sql(r => r.Title.StartsWith("100% of a_b", StringComparison.Ordinal)));

    [Fact]
    public void ACapturedLikePatternIsParameterisedAfterEscaping()
    {
        var prefix = "50%";
        var translation = Translate(r => r.Title.StartsWith(prefix, StringComparison.Ordinal));

        Assert.Equal("\"Title\" LIKE @p1 ESCAPE '\\'", translation.Sql);
        Assert.Equal("50\\%%", Assert.Single(translation.Parameters).Value);
    }

    [Fact]
    public void AnUnmodelledMethodCallIsRefusedRatherThanPostFiltered()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => Sql(r => r.Title.Insert(0, "x") == "xred"));

        Assert.Contains("Insert", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumerableAnyIsRefused()
    {
        var tags = new[] { "red" };
        var ex = Assert.Throws<NotSupportedException>(
            () => Sql(r => tags.Any(t => t == r.Tag)));

        Assert.Contains("Any", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APropertyThatMapsToNoColumnIsRefusedNamingIt()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Sql(r => r.Unmapped == "x"));

        Assert.Contains("Unmapped", ex.Message, StringComparison.Ordinal);
    }
}
