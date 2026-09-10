using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Diagnostics;

/// <summary>Default <see cref="IEdgeDiagnostics"/>: aggregates contributors, paths, startup and lifecycle.</summary>
public sealed class EdgeDiagnostics(
    IEnumerable<IEdgeDiagnosticsContributor> contributors,
    IEdgePaths paths,
    IEdgeHost host,
    IEdgeLifecycle lifecycle) : IEdgeDiagnostics
{
    /// <inheritdoc />
    public EdgeDiagnosticsReport Report()
    {
        var components = contributors
            .Select(c => new EdgeComponentReport(c.ComponentName, c.ComponentVersion, c.Describe()))
            .ToArray();

        var native = components
            .FirstOrDefault(c => c.Name.Contains("Native", StringComparison.Ordinal))
            ?.Details ?? new Dictionary<string, string?>();

        var startup = host is EdgeHost concrete ? concrete.StartupReports : [];

        return new EdgeDiagnosticsReport(
            components,
            native,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Data"] = paths.Data, ["Cache"] = paths.Cache },
            startup,
            lifecycle.RecentEvents);
    }
}

/// <summary>Renders an <see cref="EdgeDiagnosticsReport"/> for humans and for machines.</summary>
public static class EdgeDiagnosticsRenderer
{
    /// <summary>Renders the report as a support-friendly plain-text block.</summary>
    public static string ToText(EdgeDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("Qavren.Edge diagnostics");
        sb.AppendLine("=======================");

        sb.AppendLine("Paths:");
        foreach (var (key, value) in report.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(key).Append(": ").AppendLine(value);
        }

        sb.AppendLine("Components:");
        foreach (var component in report.Components)
        {
            sb.Append("  ").Append(component.Name).Append(' ').AppendLine(component.Version ?? "(no version)");
            foreach (var (key, value) in component.Details.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(key).Append(": ").AppendLine(value ?? "(null)");
            }
        }

        sb.AppendLine("Startup:");
        foreach (var task in report.Startup)
        {
            sb.Append("  [").Append(task.Order.ToString(CultureInfo.InvariantCulture)).Append("] ")
              .Append(task.Name).Append(' ')
              .Append(task.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
              .AppendLine(task.Error is null ? string.Empty : " FAILED");
            if (task.Error is not null)
            {
                sb.Append("    ").AppendLine(task.Error);
            }
        }

        sb.AppendLine("Lifecycle (most recent first):");
        foreach (var record in report.Lifecycle)
        {
            sb.Append("  ").Append(record.Timestamp.ToString("O", CultureInfo.InvariantCulture))
              .Append(' ').Append(record.Kind.ToString())
              .Append(record.Level is null ? string.Empty : "/" + record.Level.Value.ToString())
              .Append(' ').Append(record.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
              .Append(" failures=").AppendLine(record.ObserverFailures.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders the report as indented JSON. Written with <see cref="Utf8JsonWriter"/> rather than
    /// <c>JsonSerializer</c> so the assembly stays AOT- and trim-clean without a serializer context.
    /// </summary>
    public static string ToJson(EdgeDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("paths");
            foreach (var (key, value) in report.Paths)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();

            writer.WriteStartArray("components");
            foreach (var component in report.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("name", component.Name);
                writer.WriteString("version", component.Version);
                writer.WriteStartObject("details");
                foreach (var (key, value) in component.Details)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("startup");
            foreach (var task in report.Startup)
            {
                writer.WriteStartObject();
                writer.WriteString("name", task.Name);
                writer.WriteNumber("order", task.Order);
                writer.WriteNumber("durationMs", task.Duration.TotalMilliseconds);
                writer.WriteString("error", task.Error);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("lifecycle");
            foreach (var record in report.Lifecycle)
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", record.Timestamp);
                writer.WriteString("kind", record.Kind.ToString());
                writer.WriteString("level", record.Level?.ToString());
                writer.WriteNumber("durationMs", record.Duration.TotalMilliseconds);
                writer.WriteNumber("observerFailures", record.ObserverFailures);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
