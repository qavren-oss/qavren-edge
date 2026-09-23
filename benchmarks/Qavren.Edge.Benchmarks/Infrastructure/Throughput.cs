using System.Globalization;
using System.Reflection;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Qavren.Edge.Benchmarks.Infrastructure;

/// <summary>
/// Declares how many <paramref name="unit"/> one invocation of the benchmark processes, so the
/// summary can print a per-second rate beside the mean. <paramref name="source"/> names either a
/// <c>[Params]</c> member of the benchmark class or a public static property on it; the property is
/// evaluated on the HOST process, so it must be a deterministic function of the class's inputs.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ThroughputAttribute(string unit, string source) : Attribute
{
    /// <summary>What is counted: rows, docs, sentences, tokens.</summary>
    public string Unit { get; } = unit;

    /// <summary>A parameter name or a public static property name.</summary>
    public string Source { get; } = source;
}

/// <summary>One summary column per unit: <c>{unit}/s</c> = items per invocation / mean.</summary>
internal sealed class ThroughputColumn(string unit) : IColumn
{
    public static IReadOnlyList<ThroughputColumn> All { get; } =
        [new("rows"), new("docs"), new("sentences"), new("tokens")];

    public string Id => "Throughput." + unit;

    public string ColumnName => unit + "/s";

    // BenchmarkDotNet drops a non-AlwaysShow column whose cells are all equal, which is every column of a
    // one-row summary. IsAvailable already confines each column to the classes that declare its unit.
    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => "Items processed per second: " + unit + " per invocation divided by the mean";

    public bool IsAvailable(Summary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.BenchmarksCases.Any(c => Attribute(c) is not null);
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => Attribute(benchmarkCase) is null;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(benchmarkCase);

        var attribute = Attribute(benchmarkCase);
        var meanNs = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (attribute is null || meanNs is not > 0)
        {
            return "-";
        }

        var items = Items(benchmarkCase, attribute.Source);
        return items is null ? "?" : (items.Value / (meanNs.Value / 1e9)).ToString("N0", CultureInfo.InvariantCulture);
    }

    private ThroughputAttribute? Attribute(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Descriptor.WorkloadMethod
            .GetCustomAttributes<ThroughputAttribute>()
            .FirstOrDefault(a => string.Equals(a.Unit, unit, StringComparison.Ordinal));

    private static double? Items(BenchmarkCase benchmarkCase, string source)
    {
        foreach (var parameter in benchmarkCase.Parameters.Items)
        {
            if (string.Equals(parameter.Name, source, StringComparison.Ordinal))
            {
                return Convert.ToDouble(parameter.Value, CultureInfo.InvariantCulture);
            }
        }

        var property = benchmarkCase.Descriptor.Type.GetProperty(source, BindingFlags.Public | BindingFlags.Static);
        if (property is null)
        {
            return null;
        }

        // A static provider may depend on a parameter (tokens in a batch of N); it reads them here.
        var value = property.PropertyType == typeof(Func<IReadOnlyDictionary<string, object>, double>)
            ? ((Func<IReadOnlyDictionary<string, object>, double>)property.GetValue(null)!)(
                benchmarkCase.Parameters.Items.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal))
            : property.GetValue(null);
        return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
}
