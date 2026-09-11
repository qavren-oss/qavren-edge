using Microsoft.Extensions.DataIngestion;
using Xunit;

namespace Qavren.Edge.Ingestion.DataIngestion.Tests;

/// <summary>
/// <see cref="EdgeDocumentConverter"/> is lossless in both directions except for spans and images,
/// asserted field by field; images drop with exactly one 917 per document.
/// </summary>
public sealed class ConverterTests
{
    [Fact]
    public void Round_trip_preserves_every_field_except_spans()
    {
        var original = AllKinds();

        var medi = EdgeDocumentConverter.ToMedi(original);
        var back = EdgeDocumentConverter.FromMedi(medi);

        Assert.Equal(original.DocumentId, back.DocumentId);
        Assert.Equal(original.ExtractorId, back.ExtractorId);
        Assert.Equal(original.ExtractorVersion, back.ExtractorVersion);
        Assert.Equal(original.MediaType, back.MediaType);
        Assert.Equal(original.PageCount, back.PageCount);
        Assert.Equal(original.HasTextLayer, back.HasTextLayer);
        Assert.Equal(original.Metadata, back.Metadata);
        Assert.Same(original.Warnings, back.Warnings);

        Assert.Equal(original.Blocks.Count, back.Blocks.Count);
        for (var i = 0; i < original.Blocks.Count; i++)
        {
            var was = original.Blocks[i];
            var now = back.Blocks[i];
            Assert.Equal(was.Kind, now.Kind);
            Assert.Equal(was.HeadingLevel, now.HeadingLevel);
            Assert.Equal(was.PageNumber, now.PageNumber);
            Assert.Equal(original.Text[was.Start..was.End], back.Text[now.Start..now.End]);
        }

        // Spans are the documented loss: the rebuilt buffer is blank-line joined, so at least one
        // offset moved even though every slice survived.
        Assert.NotEqual(original.Text, back.Text);
        Assert.Contains(original.Blocks.Zip(back.Blocks), pair => pair.First.Start != pair.Second.Start);
    }

    [Fact]
    public void ToMedi_uses_native_element_types_where_MEDI_has_them()
    {
        var medi = EdgeDocumentConverter.ToMedi(AllKinds());
        var root = Assert.Single(medi.Sections);

        Assert.Equal("h1", root.Metadata[EdgeDocumentConverter.DocumentMetadataPrefix + "title"]);
        Assert.Equal("test", root.Metadata[EdgeDocumentConverter.ExtractorIdKey]);
        Assert.Equal(7, root.Metadata[EdgeDocumentConverter.ExtractorVersionKey]);
        Assert.Equal(3, root.Metadata[EdgeDocumentConverter.PageCountKey]);
        Assert.False(Assert.IsType<bool>(root.Metadata[EdgeDocumentConverter.HasTextLayerKey]));

        var elements = root.Elements;
        var header = Assert.IsType<IngestionDocumentHeader>(elements[0]);
        Assert.Equal(2, header.Level);
        Assert.Equal(1, header.PageNumber);
        Assert.IsType<IngestionDocumentParagraph>(elements[1]);
        Assert.False(elements[1].HasMetadata);

        // Two consecutive rows became ONE table, rows x 1.
        var table = Assert.IsType<IngestionDocumentTable>(elements[2]);
        Assert.Equal(2, table.Cells.GetLength(0));
        Assert.Equal(1, table.Cells.GetLength(1));
        Assert.Equal("| a | b |", table.Cells[0, 0]!.Text);
        Assert.Equal("| 1 | 2 |", table.Cells[1, 0]!.Text);

        foreach (var (index, kind) in new[] { (3, "ListItem"), (4, "Code"), (5, "Caption"), (6, "Quote") })
        {
            var tagged = Assert.IsType<IngestionDocumentParagraph>(elements[index]);
            Assert.Equal(kind, tagged.Metadata[EdgeDocumentConverter.BlockKindKey]);
        }

        Assert.IsType<IngestionDocumentFooter>(elements[7]);
        Assert.DoesNotContain(elements, e => e is IngestionDocumentImage);
    }

    [Fact]
    public void Images_drop_with_one_917_per_document_not_per_image()
    {
        var document = new IngestionDocument("pictures");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("before"));
        section.Elements.Add(new IngestionDocumentImage("![one](a.png)") { AlternativeText = "one" });
        section.Elements.Add(new IngestionDocumentImage("![two](b.png)") { AlternativeText = "two" });
        var nested = new IngestionDocumentSection();
        nested.Elements.Add(new IngestionDocumentImage("![three](c.png)"));
        nested.Elements.Add(new IngestionDocumentParagraph("after"));
        section.Elements.Add(nested);
        document.Sections.Add(section);

        var logger = new CapturingLogger();
        var extracted = EdgeDocumentConverter.FromMedi(document, logger);

        Assert.Equal(2, extracted.Blocks.Count);
        Assert.Equal("before\n\nafter", extracted.Text);
        Assert.Equal(1, logger.Count(EdgeIngestionEventIds.MediImagesDropped));
        Assert.Contains("3", logger.Records.Single(r => r.EventId == 917).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_images_means_no_917()
    {
        var logger = new CapturingLogger();
        EdgeDocumentConverter.FromMedi(EdgeDocumentConverter.ToMedi(AllKinds()), logger);
        Assert.Equal(0, logger.Count(EdgeIngestionEventIds.MediImagesDropped));
    }

    [Fact]
    public void A_MEDI_native_document_gets_defaults_and_its_tables_become_rows()
    {
        var document = new IngestionDocument("native.html");
        var section = new IngestionDocumentSection();
        section.Metadata["source"] = "reader-x";
        section.Elements.Add(new IngestionDocumentHeader("# Title") { Level = 1, Text = "Title" });
        var cells = new IngestionDocumentElement[2, 2];
        cells[0, 0] = new IngestionDocumentParagraph("h1");
        cells[0, 1] = new IngestionDocumentParagraph("h2");
        cells[1, 0] = new IngestionDocumentParagraph("v1") { PageNumber = 4 };
        cells[1, 1] = null!;
        section.Elements.Add(new IngestionDocumentTable("| h1 | h2 |\n| v1 |  |", cells));
        document.Sections.Add(section);

        var extracted = EdgeDocumentConverter.FromMedi(document);

        Assert.Equal(EdgeDocumentConverter.DefaultExtractorId, extracted.ExtractorId);
        Assert.Equal(1, extracted.ExtractorVersion);
        Assert.Equal(EdgeDocumentConverter.DefaultMediaType, extracted.MediaType);
        Assert.Null(extracted.PageCount);
        Assert.True(extracted.HasTextLayer);
        Assert.Equal("reader-x", extracted.Metadata["source"]);

        Assert.Equal(3, extracted.Blocks.Count);
        Assert.Equal(DocumentBlockKind.Heading, extracted.Blocks[0].Kind);
        Assert.Equal(1, extracted.Blocks[0].HeadingLevel);
        Assert.Equal("Title", Slice(extracted, 0));
        Assert.Equal(DocumentBlockKind.TableRow, extracted.Blocks[1].Kind);
        Assert.Equal("h1 | h2", Slice(extracted, 1));
        Assert.Equal("v1", Slice(extracted, 2));
        Assert.Equal(4, extracted.Blocks[2].PageNumber);
    }

    [Fact]
    public void Every_kind_is_mapped_and_none_falls_back_to_paragraph()
    {
        foreach (var kind in Enum.GetValues<DocumentBlockKind>())
        {
            var text = kind == DocumentBlockKind.TableRow ? "| x |" : "body";
            var document = new ExtractedDocument(
                "one", "test", 1, IngestionMediaTypes.PlainText, text,
                [new DocumentBlock(kind, 0, text.Length)],
                new Dictionary<string, string>(StringComparer.Ordinal));

            var back = EdgeDocumentConverter.FromMedi(EdgeDocumentConverter.ToMedi(document));

            var block = Assert.Single(back.Blocks);
            Assert.Equal(kind, block.Kind);
            Assert.Equal(text, back.Text);
        }
    }

    private static string Slice(ExtractedDocument document, int index) =>
        document.Text[document.Blocks[index].Start..document.Blocks[index].End];

    /// <summary>One block of every kind, offsets hand-computed against <c>Text</c>.</summary>
    private static ExtractedDocument AllKinds()
    {
        const string text =
            "## Heading\n" +          // 0..10
            "A paragraph.\n" +        // 11..23
            "| a | b |\n" +           // 24..33
            "| 1 | 2 |\n" +           // 34..43
            "- item\n" +              // 44..50
            "    code\n" +            // 51..59
            "Figure 1\n" +            // 60..68
            "> quote\n" +             // 69..76
            "footer";                 // 77..83

        return new ExtractedDocument(
            "all-kinds.md",
            "test",
            7,
            IngestionMediaTypes.Markdown,
            text,
            [
                new DocumentBlock(DocumentBlockKind.Heading, 0, 10, HeadingLevel: 2, PageNumber: 1),
                new DocumentBlock(DocumentBlockKind.Paragraph, 11, 23, PageNumber: 1),
                new DocumentBlock(DocumentBlockKind.TableRow, 24, 33, PageNumber: 2),
                new DocumentBlock(DocumentBlockKind.TableRow, 34, 43, PageNumber: 2),
                new DocumentBlock(DocumentBlockKind.ListItem, 44, 50),
                new DocumentBlock(DocumentBlockKind.Code, 51, 59),
                new DocumentBlock(DocumentBlockKind.Caption, 60, 68, PageNumber: 3),
                new DocumentBlock(DocumentBlockKind.Quote, 69, 76),
                new DocumentBlock(DocumentBlockKind.Footer, 77, 83, PageNumber: 3),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "h1", ["lang"] = "en" },
            PageCount: 3,
            HasTextLayer: false,
            Warnings: [new IngestionFailure(EdgeErrorCode.ExtractionFailed, "a warning", "test")]);
    }
}
