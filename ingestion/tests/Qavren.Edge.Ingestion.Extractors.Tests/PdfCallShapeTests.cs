using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// Spec 7.4's call-shape rules, asserted two ways: a source grep of the satellite (the honest
/// version — IL reflection would miss a call behind a helper), and a behavioural test that counts
/// what is read through the stream.
/// </summary>
public class PdfCallShapeTests
{
    private const string PdfSatellite = "Qavren.Edge.Ingestion.Pdf";

    [Fact]
    public void PdfDocumentIsOpenedThroughTheStreamOverloadOnly()
    {
        var root = RepoSources.Locate(PdfSatellite);
        if (root is null)
        {
            Assert.Skip("The repository source tree is not present (device lane or published output); nothing to grep.");
            return;
        }

        var opens = 0;
        var offenders = new List<string>();
        foreach (var file in RepoSources.SourceFiles(root))
        {
            foreach (var line in RepoSources.CodeLines(file))
            {
                var at = 0;
                while ((at = line.IndexOf("PdfDocument.Open(", at, StringComparison.Ordinal)) >= 0)
                {
                    opens++;
                    var argument = line[(at + "PdfDocument.Open(".Length)..].TrimStart();
                    if (argument.StartsWith('"')
                        || argument.StartsWith("path", StringComparison.Ordinal)
                        || argument.StartsWith("File.", StringComparison.Ordinal)
                        || argument.StartsWith("filePath", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                    }

                    at += "PdfDocument.Open(".Length;
                }

                // The path overload's hidden File.ReadAllBytes is the whole objection; the explicit
                // call is banned on the same grounds (spec 7.1).
                if (line.Contains("File.ReadAllBytes", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.True(opens > 0, "the grep is pointed at something real: at least one PdfDocument.Open( call");
        Assert.Empty(offenders);
    }

    [Fact]
    public void GetPagesIsNeverMaterialisedAndExportIsNeverReferenced()
    {
        var root = RepoSources.Locate(PdfSatellite);
        if (root is null)
        {
            Assert.Skip("The repository source tree is not present (device lane or published output); nothing to grep.");
            return;
        }

        var getPages = 0;
        var offenders = new List<string>();
        foreach (var file in RepoSources.SourceFiles(root))
        {
            foreach (var line in RepoSources.CodeLines(file))
            {
                if (line.Contains("GetPages()", StringComparison.Ordinal))
                {
                    getPages++;
                }

                if (line.Contains("GetPages().ToList", StringComparison.Ordinal)
                    || line.Contains("GetPages().ToArray", StringComparison.Ordinal)
                    || line.Contains("DocumentLayoutAnalysis.Export", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.True(getPages > 0, "the grep is pointed at something real: GetPages() is called");
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task TheWholeFileIsNotReadUpFront()
    {
        // One MiB of padding in an object nothing references. A parser that follows the
        // cross-reference table never reads it; a parser that slurps the file reads all of it.
        var bytes = TestPdf.Padded();
        CountingSeekableStream? counting = null;
        var item = new DocumentSourceItem(
            "padded.pdf",
            IngestionMediaTypes.Pdf,
            _ => ValueTask.FromResult<Stream>(counting = new CountingSeekableStream(bytes)),
            SizeBytes: bytes.Length);

        var (document, _) = await PdfExtractorTests.ExtractAsync(item);

        Assert.NotNull(counting);
        Assert.StartsWith("Hello from a minimal PDF.", document.Text, StringComparison.Ordinal);
        Assert.True(
            counting.BytesRead < bytes.Length / 4,
            $"read {counting.BytesRead} of {bytes.Length} bytes; the padding object was not skipped");
        Assert.True(counting.SeekCount > 0, "the parser seeks through the stream rather than draining it");
    }
}
