using System.Globalization;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Where a measurement goes when it is recorded rather than asserted: the test output, the GitHub
/// job summary when <c>GITHUB_STEP_SUMMARY</c> is set, and the nightly artifact directory the
/// <c>chat-model-tests</c> job uploads (<c>artifacts/chat-nightly/**</c> under the workspace).
/// </summary>
/// <remarks>
/// Spec 16.3 records tokens/sec, TTFT and peak RSS as artifacts, not assertions, because there is
/// no measured device baseline to compare a hosted-runner number against; spec 16.2 prints the
/// guidance probe's verdict whichever way it lands. Both need one place to write to, and a test
/// that <c>Console.WriteLine</c>s a number into a log nobody keeps has not recorded anything.
/// </remarks>
internal static class JobSummary
{
    /// <summary>The directory the nightly workflow uploads, or a temp directory off CI.</summary>
    public static string ArtifactDirectory { get; } = ResolveArtifactDirectory();

    /// <summary>Records one <c>key = value</c> line under a heading.</summary>
    /// <param name="section">The artifact file and summary heading, e.g. <c>tier2-guidance</c>.</param>
    /// <param name="key">The measurement's name.</param>
    /// <param name="value">Its value.</param>
    public static void Record(string section, string key, object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)";
        var line = $"{key} = {text}";

        TestContext.Current.TestOutputHelper?.WriteLine($"[{section}] {line}");

        try
        {
            Directory.CreateDirectory(ArtifactDirectory);
            File.AppendAllText(Path.Combine(ArtifactDirectory, section + ".txt"), line + "\n");
        }
        catch (IOException)
        {
            // A read-only or missing artifact root must not fail a measurement.
        }
        catch (UnauthorizedAccessException)
        {
            // Same, the other spelling.
        }

        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            try
            {
                File.AppendAllText(summary, $"- `{section}` {line}\n");
            }
            catch (IOException)
            {
                // The summary file is GitHub's; a failure to append is not a test failure.
            }
        }
    }

    /// <summary>Records a whole block of text - a diagnostics dump - under a heading.</summary>
    /// <param name="section">The artifact file and summary heading.</param>
    /// <param name="block">The text.</param>
    public static void RecordBlock(string section, string block)
    {
        TestContext.Current.TestOutputHelper?.WriteLine($"[{section}]\n{block}");

        try
        {
            Directory.CreateDirectory(ArtifactDirectory);
            File.AppendAllText(Path.Combine(ArtifactDirectory, section + ".txt"), block + "\n");
        }
        catch (IOException)
        {
            // See Record.
        }
        catch (UnauthorizedAccessException)
        {
            // See Record.
        }
    }

    private static string ResolveArtifactDirectory()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        return workspace is { Length: > 0 }
            ? Path.Combine(workspace, "artifacts", "chat-nightly")
            : Path.Combine(Path.GetTempPath(), "qedge-chat-nightly");
    }
}
