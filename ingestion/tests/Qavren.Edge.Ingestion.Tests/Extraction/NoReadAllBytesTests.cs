using System.Runtime.CompilerServices;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// The assertion spec 7.1 asks for: <c>File.ReadAllBytes</c> and <c>File.ReadAllText</c> appear
/// NOWHERE under <c>ingestion/src/**</c>. Every extractor opens its source through
/// <c>DocumentSourceItem.OpenAsync</c>, and the whole streaming posture collapses the moment one
/// of them slurps a file into a byte array.
/// </summary>
/// <remarks>
/// A source grep is the honest version - IL reflection would miss a call behind a helper and would
/// not flag the string in a new file at all. The repo is located from THIS file's compile-time
/// path rather than from <c>AppContext.BaseDirectory</c>, because the parallel phases redirect
/// <c>bin/</c> under <c>-p:ArtifactsPath</c>, outside the repo entirely. On a device lane there is
/// no source tree and the test reports that instead of failing.
/// </remarks>
public class NoReadAllBytesTests
{
    private static readonly string[] Banned = ["File.ReadAllBytes", "File.ReadAllText", "File.ReadAllLines"];

    [Fact]
    public void NoSourceFileUnderIngestionSrcSlurpsAWholeFile()
    {
        var srcRoot = LocateIngestionSrc();
        if (srcRoot is null)
        {
            Assert.Skip("The repository source tree is not present (device lane or published output); nothing to grep.");
            return;
        }

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in StripComments(File.ReadAllLines(file)))
            {
                foreach (var banned in Banned)
                {
                    if (line.Contains(banned, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(srcRoot, file)}: {banned}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheGrepItselfIsPointedAtSomethingReal()
    {
        var srcRoot = LocateIngestionSrc();
        if (srcRoot is null)
        {
            Assert.Skip("The repository source tree is not present (device lane or published output).");
            return;
        }

        // A grep that matches nothing because it is pointed at an empty directory is not evidence.
        Assert.NotEmpty(Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The ban is on the CALL, not on the mention: <c>IDocumentExtractor</c>'s XML doc states the
    /// rule and would otherwise indict itself. Comment lines are dropped before the match.
    /// </summary>
    private static IEnumerable<string> StripComments(IEnumerable<string> lines)
    {
        var inBlockComment = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (inBlockComment)
            {
                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            yield return line;
        }
    }

    private static string? LocateIngestionSrc([CallerFilePath] string callerFilePath = "")
    {
        var directory = Path.GetDirectoryName(callerFilePath);
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, "ingestion", "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
