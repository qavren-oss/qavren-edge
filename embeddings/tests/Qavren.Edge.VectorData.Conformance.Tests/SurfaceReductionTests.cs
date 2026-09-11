using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Conformance.Tests.Fixtures;
using Qavren.Edge.VectorData.Internal;
using VectorData.ConformanceTests;
using Xunit;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// The gate runs a <b>narrowed</b> suite in two places that produce no skip and therefore do not
/// show in the skip count: <see cref="EdgeFilterFixture"/> drops two properties from the suite's
/// <c>FilterRecord</c>, and <see cref="EdgeDataTypeFixture"/> suppresses three columns from the
/// suite's <c>DefaultRecord</c>. Both are legitimate - the types are not storable by this provider -
/// but "legitimate" is a claim, and a reader of a green run has no way to check it.
/// <para>
/// These tests are that check. Each reduction is asserted to be <b>exactly</b> what is documented in
/// <c>SkipList.md</c> and to be <b>forced</b> by <see cref="SqliteTypeMap"/> rather than chosen: a
/// dropped property whose type the provider can actually store fails here, and so does a property
/// that silently appears or disappears when the conformance package is upgraded.
/// </para>
/// </summary>
public class SurfaceReductionTests
{
    /// <summary>The two <c>FilterRecord</c> properties <see cref="EdgeFilterFixture"/> omits.</summary>
    private static readonly string[] DroppedFilterProperties =
        [
            nameof(FilterTests<string>.FilterRecord.StringArray),
            nameof(FilterTests<string>.FilterRecord.StringList),
        ];

    /// <summary>The three <c>DefaultRecord</c> column types <see cref="EdgeDataTypeFixture"/> suppresses.</summary>
    private static readonly Type[] SuppressedDataTypes = [typeof(byte), typeof(decimal), typeof(string[])];

    /// <summary>
    /// The filter fixture's record definition differs from the suite's own <c>FilterRecord</c> by
    /// exactly the two documented properties - no more (a wider reduction would quietly shrink the
    /// gate) and no fewer (a narrower one would mean the skips in <see cref="EdgeFilterTests"/> are
    /// no longer needed).
    /// </summary>
    [Fact]
    public void EdgeFilterFixtureDropsExactlyTheTwoDocumentedProperties()
    {
        var declared = new EdgeFilterFixture().CreateRecordDefinition().Properties
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var onTheRecord = typeof(FilterTests<string>.FilterRecord)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(declared.Except(onTheRecord, StringComparer.Ordinal));
        Assert.Equal(
            DroppedFilterProperties.OrderBy(n => n, StringComparer.Ordinal),
            onTheRecord.Except(declared, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The drop is forced, not chosen: neither dropped property's CLR type has a SQLite column
    /// mapping, while every property the fixture keeps does.
    /// </summary>
    [Fact]
    public void TheDroppedFilterPropertiesAreOnesThisProviderCannotStore()
    {
        var record = typeof(FilterTests<string>.FilterRecord);

        foreach (var name in DroppedFilterProperties)
        {
            var type = record.GetProperty(name)!.PropertyType;
            Assert.False(
                SqliteTypeMap.TryGetColumnType(type, out _),
                $"'{name}' is dropped from the conformance record definition, but SqliteTypeMap can store " +
                $"'{type}'. The drop is then a reduction of the gate with no cause - remove it.");
        }

        foreach (var property in new EdgeFilterFixture().CreateRecordDefinition().Properties
                     .OfType<VectorStoreDataProperty>())
        {
            var declared = property.Type;
            Assert.NotNull(declared);
            Assert.True(
                SqliteTypeMap.TryGetColumnType(declared, out _),
                $"'{property.Name}' is kept in the record definition but SqliteTypeMap cannot store '{declared}'.");
        }
    }

    /// <summary>
    /// The vector property is declared non-nullable. vec0 overloads SQL <c>NULL</c> on a vector
    /// column to mean "no change", so the provider rejects a nullable vector property at model build
    /// (spec §12.1); the suite never writes a null vector, so nothing it asserts changes.
    /// </summary>
    [Fact]
    public void TheFilterFixtureVectorPropertyIsDeclaredNonNullable()
    {
        var vector = Assert.Single(
            new EdgeFilterFixture().CreateRecordDefinition().Properties
                .OfType<VectorStoreVectorProperty>());

        var declared = vector.Type;
        Assert.NotNull(declared);
        Assert.Equal(typeof(ReadOnlyMemory<float>), declared);
        Assert.Null(Nullable.GetUnderlyingType(declared));
    }

    /// <summary>
    /// <c>DataTypeTests</c> reads <c>UnsupportedDefaultTypes</c> when it builds the record
    /// definition, so a type listed there never gets a column and its assertion is a silent no-op
    /// rather than a skip. This pins the list to the three documented types.
    /// </summary>
    [Fact]
    public void EdgeDataTypeFixtureSuppressesExactlyTheThreeDocumentedTypes() =>
        Assert.Equal(
            SuppressedDataTypes.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal),
            new EdgeDataTypeFixture().UnsupportedDefaultTypes.Select(t => t.FullName)
                .OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>
    /// And the suppression is forced, not chosen: none of the three has a SQLite column mapping, so
    /// declaring the column would fail at model build rather than produce a false pass.
    /// </summary>
    [Fact]
    public void TheSuppressedDataTypesAreOnesThisProviderCannotStore()
    {
        foreach (var type in new EdgeDataTypeFixture().UnsupportedDefaultTypes)
        {
            Assert.False(
                SqliteTypeMap.TryGetColumnType(type, out _),
                $"'{type}' is declared unsupported by the conformance fixture, but SqliteTypeMap can store it. " +
                "The suppression is then a reduction of the gate with no cause - remove it.");
        }
    }
}
