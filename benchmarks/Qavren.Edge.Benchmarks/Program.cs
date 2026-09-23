using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Qavren.Edge.Benchmarks.Chat;
using Qavren.Edge.Benchmarks.Embeddings;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.Benchmarks.Ingestion;
using Qavren.Edge.Benchmarks.Sqlite;
using Qavren.Edge.Benchmarks.VectorData;

namespace Qavren.Edge.Benchmarks;

/// <summary>
/// The suite's entry point (docs/superpowers/specs/2026-09-23-sp5-benchmarks-design.md 3.3).
/// <list type="bullet">
/// <item>BenchmarkDotNet has no skip, so this selects the classes: Sqlite, VectorData and
/// Ingestion always; Embeddings only with <c>QAVREN_EDGE_MODEL_DIR</c>; Chat only with
/// <c>QAVREN_EDGE_CHAT_MODEL_DIR</c>. Each skipped area prints one line. <c>--list</c> sees every
/// class, because listing runs nothing.</item>
/// <item>The default job is <c>ShortRun</c>; <c>--full</c> uses BenchmarkDotNet's default job.
/// With no <c>--filter</c> the whole selection runs, so a bare <c>dotnet run</c> is the CI run.</item>
/// <item>Reports (JSON and GitHub Markdown) land in <c>artifacts/benchmarks/</c> at the repository
/// root.</item>
/// </list>
/// Every other argument passes through to <see cref="BenchmarkSwitcher"/>.
/// </summary>
internal static class Program
{
    private static readonly Type[] AlwaysOn =
    [
        typeof(VecKnnBenchmarks),
        typeof(FtsMatchBenchmarks),
        typeof(VecInsertBenchmarks),
        typeof(VectorSearchBenchmarks),
        typeof(IngestionBenchmarks),
    ];

    private static readonly Type[] Embeddings = [typeof(TokenizerBenchmarks), typeof(EmbeddingBenchmarks)];

    private static readonly Type[] Chat = [typeof(ChatBenchmarks)];

    private static int Main(string[] args)
    {
        // --full, or an explicit BenchmarkDotNet --job, replaces the ShortRun default.
        var full = args.Contains("--full", StringComparer.Ordinal)
                   || args.Any(a => a.StartsWith("--job", StringComparison.Ordinal) || a == "-j");
        var passThrough = args.Where(a => !string.Equals(a, "--full", StringComparison.Ordinal)).ToList();
        var listing = passThrough.Any(a => a.StartsWith("--list", StringComparison.Ordinal));

        var types = new List<Type>(AlwaysOn);
        if (listing || EmbeddingBenchmarks.ModelAvailable)
        {
            types.AddRange(Embeddings);
        }
        else
        {
            Console.WriteLine("skipped: Embeddings (" + EmbeddingBenchmarks.ModelDirVariable +
                              " unset or missing onnx/model_qint8_arm64.onnx and vocab.txt)");
        }

        if (listing || ChatBenchmarks.ModelAvailable)
        {
            types.AddRange(Chat);
        }
        else
        {
            Console.WriteLine("skipped: Chat (" + ChatBenchmarks.ModelDirVariable +
                              " unset or missing genai_config.json)");
        }

        if (!listing && !passThrough.Any(a => a.StartsWith("--filter", StringComparison.Ordinal) || a == "-f"))
        {
            passThrough.AddRange(["--filter", "*"]);
        }

        var summaries = BenchmarkSwitcher.FromTypes([.. types]).Run([.. passThrough], Config(full));
        return summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
    }

    private static ManualConfig Config(bool full)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .WithArtifactsPath(Path.Combine(RepositoryRoot(), "artifacts", "benchmarks"))
            .AddExporter(JsonExporter.Full)
            .AddExporter(MarkdownExporter.GitHub)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn([.. ThroughputColumn.All]);

        return full ? config : config.AddJob(Job.ShortRun);
    }

    /// <summary>The directory holding <c>QavrenEdge.slnx</c>, found by walking up; else the working directory.</summary>
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "QavrenEdge.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        return Environment.CurrentDirectory;
    }
}
