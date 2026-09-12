using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Qavren.Edge.Ingestion.Pdf;
using UglyToad.PdfPig.Fonts.SystemFonts;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// Spec 14.6's extractor-owned device assertions (plan Task 7.3): the PdfPig asset trap, and the
/// runtime DOCX zip of spec 17 item 10. Both are about what the DEVICE runtime actually loaded and
/// can actually do, which is why nothing on the host can stand in for them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The asset trap.</b> PdfPig ships no <c>net10.0</c> TFM, so every TFM here must resolve
/// <c>lib/net9.0</c>. The <c>netstandard2.0</c> copy of <c>UglyToad.PdfPig.Fonts.dll</c> contains
/// neither <c>AndroidSystemFontLister</c> nor <c>IOSSystemFontLister</c> (verified against the
/// 0.1.16 package bytes) and its <c>SystemFontFinder</c> static constructor throws
/// <c>NotSupportedException</c> on the first PDF that references a non-embedded font. That
/// surfaces as a <see cref="TypeInitializationException"/> with no compile error anywhere.
/// <c>ci.yml</c>'s "Assert PdfPig asset resolution" step catches the resolution in
/// <c>project.assets.json</c>; these facts catch the consequence on the runtime that matters.
/// </para>
/// <para>
/// <b>Spec 17 item 10 - branch taken: runtime zip.</b> The DOCX fixtures reach the device as
/// embedded part XML and are zipped with <see cref="ZipArchive"/> in <c>Create</c> mode over a
/// <see cref="MemoryStream"/> on the device runtime itself, through the same
/// <see cref="DeterministicOpc"/> the host tests use. The fallback the plan names (a pre-zipped
/// embedded blob with the determinism assertion host-only) is NOT taken; if
/// <see cref="TheDocxZipAssemblesDeterministicallyOnThisRuntime"/> fails on some lane, that
/// failure is the signal to take it.
/// </para>
/// <para>
/// This file compiles for EVERY TFM, <c>net10.0</c> included - only its
/// <see cref="DeviceFactAttribute"/>s skip on the host. It uses no platform API, so it needs no
/// <c>Platforms\**</c> compile guard in the csproj.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification =
        "xunit ships a CA2007 suppressor for test methods, but it recognises [Fact]/[Theory] " +
        "literally and not a derived attribute, so every await under [DeviceFact] trips the rule " +
        "on the net10.0 lane (the device lanes already NoWarn it in the csproj). Its fix - " +
        "ConfigureAwait(false) in a test body - is what xUnit1030 forbids.")]
public sealed class DeviceExtractorFacts
{
    private const string MinimalPdf = "pdf/minimal-text.pdf";
    private const string MinimalFirstLine = "Hello from a minimal PDF.";
    private const string MinimalSecondLine = "A second line of the same paragraph.";

    private const string RunSplitTree = "run-split";
    private const string RunSplitSentence = "One sentence split across five separate runs with rsid noise.";

    /// <summary>The seven committed PDFs, by name (plan Task 1.3 Step 7).</summary>
    private static readonly string[] CommittedPdfs =
    [
        "pdf/broken-startxref.pdf",
        "pdf/hyphen-linebreak.pdf",
        "pdf/minimal-text.pdf",
        "pdf/no-text-layer.pdf",
        "pdf/two-columns.pdf",
        "pdf/two-pages.pdf",
        "pdf/xref-stream.pdf",
    ];

    /// <summary>The eleven committed DOCX trees, by name (plan adjustment 13).</summary>
    private static readonly string[] CommittedDocxTrees =
    [
        "empty-body",
        "footnotes",
        "header-footer",
        "headings",
        "hyperlink",
        "localised-style",
        "numbered-list",
        "run-split",
        "table",
        "textbox",
        "unknown-style",
    ];

    /// <summary>
    /// The trap at its source, with no PDF in the way: touching <see cref="SystemFontFinder.Instance"/>
    /// runs the static constructor that picks a platform font lister. On the <c>netstandard2.0</c>
    /// build there is no Android or iOS branch to pick, and this is where it throws. The assembly
    /// that was actually loaded is then named by its own <see cref="TargetFrameworkAttribute"/>,
    /// so a failure here says WHICH copy got linked rather than only that something did.
    /// </summary>
    [DeviceFact]
    public void TheSystemFontFinderStaticConstructorRunsOnThisRuntime()
    {
        var fonts = typeof(SystemFontFinder).Assembly;
        var framework = fonts.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        var thrown = Record.Exception(() => _ = SystemFontFinder.Instance);

        Assert.True(
            thrown is null,
            $"SystemFontFinder's static constructor threw on {RuntimeInformation.OSDescription} " +
            $"with UglyToad.PdfPig.Fonts built for '{framework ?? "(no TargetFrameworkAttribute)"}' " +
            $"at '{fonts.Location}'. This is spec 14.6's PdfPig asset trap: the netstandard2.0 " +
            "copy got linked instead of lib/net9.0. Chain: " + Describe(thrown));

        Assert.NotNull(framework);
        Assert.StartsWith(".NETCoreApp,", framework, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trap as a consumer meets it: <c>minimal-text.pdf</c> references Helvetica and embeds
    /// nothing, and it must open and extract under the extractor's defaults.
    /// </summary>
    [DeviceFact]
    public async Task ANonEmbeddedHelveticaPdfExtractsUnderTheDefaultsOnThisRuntime()
    {
        await AssertMinimalPdfExtracts(options: null);
    }

    /// <summary>
    /// The same PDF with <see cref="PdfExtractorOptions.SkipMissingFonts"/> off, which is the
    /// setting that lets PdfPig go to the system font lister for a name it does not have. The
    /// static constructor fires on first touch either way; the flag only changes how far past it
    /// the parse gets, so both settings are asserted.
    /// </summary>
    [DeviceFact]
    public async Task ANonEmbeddedHelveticaPdfExtractsWithSkipMissingFontsOffOnThisRuntime()
    {
        await AssertMinimalPdfExtracts(new PdfExtractorOptions { SkipMissingFonts = false });
    }

    /// <summary>
    /// Spec 17 item 10: <see cref="ZipArchive"/> in <c>Create</c> mode over a
    /// <see cref="MemoryStream"/> on this runtime, twice, byte-identical - and the result reads
    /// back as a zip whose entries are the committed parts in the declared order with their
    /// committed bytes. This is the determinism assertion the fallback branch would have kept
    /// host-only; it runs on the device because the runtime-zip branch was taken.
    /// </summary>
    [DeviceFact]
    public void TheDocxZipAssemblesDeterministicallyOnThisRuntime()
    {
        var parts = DeterministicOpc.Parts(RunSplitTree);

        var first = DeterministicOpc.Build(parts);
        var second = DeterministicOpc.Build(parts);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);

        using var stream = new MemoryStream(first, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(parts.Select(p => p.Name).ToArray(), zip.Entries.Select(e => e.FullName).ToArray());

        foreach (var (name, content) in parts)
        {
            var entry = zip.GetEntry(name);
            Assert.NotNull(entry);

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);

            Assert.Equal(content, buffer.ToArray());
        }
    }

    /// <summary>
    /// The embedded-resource path end to end for DOCX: committed part XML, zipped at runtime on
    /// this device, opened by DocumentFormat.OpenXml, extracted - and the whole 61-character
    /// sentence comes back as one block, by equality.
    /// </summary>
    [DeviceFact]
    public async Task ARuntimeZippedDocxExtractsFromEmbeddedPartsOnThisRuntime()
    {
        var (document, _) = await DocxExtractorTests.ExtractAsync(RunSplitTree);

        Assert.Equal("docx", document.ExtractorId);
        Assert.True(document.HasTextLayer);
        Assert.Equal(RunSplitSentence, document.Text);

        var block = Assert.Single(document.Blocks);
        Assert.Equal(DocumentBlockKind.Paragraph, block.Kind);
        Assert.Equal(RunSplitSentence, TestHarness.Slice(document, block));
    }

    /// <summary>
    /// The corpus made it into the DEVICE assembly: seven PDFs and eleven DOCX trees, by name. A
    /// device lane that loads this assembly with a partial manifest would otherwise fail the facts
    /// above with a "not embedded" message that blames the fixture rather than the build.
    /// </summary>
    [DeviceFact]
    public void TheCommittedCorpusIsEmbeddedInThisAssembly()
    {
        var pdfs = FixtureCorpus.Under("pdf/")
            .Where(p => p.EndsWith(".pdf", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(CommittedPdfs, pdfs);

        var trees = FixtureCorpus.Under("docx/")
            .Select(p => p["docx/".Length..])
            .Select(p => p[..p.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CommittedDocxTrees, trees);
    }

    private static async Task AssertMinimalPdfExtracts(PdfExtractorOptions? options)
    {
        ExtractedDocument? document = null;
        var thrown = await Record.ExceptionAsync(async () =>
        {
            (document, _) = await PdfExtractorTests.ExtractAsync(FixtureCorpus.Item(MinimalPdf), options)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        Assert.True(
            thrown is null,
            $"Extracting {MinimalPdf} (Helvetica, not embedded) threw on {RuntimeInformation.OSDescription} " +
            $"with SkipMissingFonts = {(options ?? new PdfExtractorOptions()).SkipMissingFonts}. " +
            "If the chain below carries TypeInitializationException -> NotSupportedException, this is " +
            "spec 14.6's PdfPig asset trap: the netstandard2.0 UglyToad.PdfPig.Fonts got linked. Chain: " +
            Describe(thrown));

        Assert.NotNull(document);
        Assert.Equal("pdf", document.ExtractorId);
        Assert.Equal(1, document.PageCount);
        Assert.True(document.HasTextLayer);
        Assert.StartsWith(MinimalFirstLine, document.Text, StringComparison.Ordinal);
        Assert.Contains(MinimalSecondLine, document.Text, StringComparison.Ordinal);

        var block = Assert.Single(document.Blocks);
        Assert.Equal(DocumentBlockKind.Paragraph, block.Kind);
        Assert.Equal(document.Text, TestHarness.Slice(document, block));
    }

    /// <summary>Type and message of every exception in the chain, outermost first.</summary>
    private static string Describe(Exception? exception)
    {
        if (exception is null)
        {
            return "(none)";
        }

        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().FullName}: {current.Message}");
        }

        return string.Join(" -> ", parts);
    }
}
