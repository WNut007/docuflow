using System.Text;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using Xunit;
using Layout = Google.Cloud.DocumentAI.V1.Document.Types.Page.Types.Layout;
using GcpDocument = Google.Cloud.DocumentAI.V1.Document;
using Page = Google.Cloud.DocumentAI.V1.Document.Types.Page;
using FormField = Google.Cloud.DocumentAI.V1.Document.Types.Page.Types.FormField;
using Line = Google.Cloud.DocumentAI.V1.Document.Types.Page.Types.Line;
using Table = Google.Cloud.DocumentAI.V1.Document.Types.Page.Types.Table;
using TableRow = Google.Cloud.DocumentAI.V1.Document.Types.Page.Types.Table.Types.TableRow;
using TableCell = Google.Cloud.DocumentAI.V1.Document.Types.Page.Types.Table.Types.TableCell;
using TextAnchor = Google.Cloud.DocumentAI.V1.Document.Types.TextAnchor;
using TextSegment = Google.Cloud.DocumentAI.V1.Document.Types.TextAnchor.Types.TextSegment;
using Google.Cloud.DocumentAI.V1;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline test for the shared Document-proto -> OcrExtraction mapper (no GCP/network). Builds a
/// synthetic Document AI proto and asserts the mapping both the online and batch paths reuse.
/// </summary>
public sealed class DocumentAiMapperTests
{
    // Accumulates Document.Text and hands out (start, length) for each appended segment.
    private sealed class TextBuilder
    {
        private readonly StringBuilder _sb = new();
        public (int start, int len) Add(string s) { int start = _sb.Length; _sb.Append(s); return (start, s.Length); }
        public string Text => _sb.ToString();
    }

    private static Layout Lay((int start, int len) seg, float? l = null, float? t = null, float? w = null, float? h = null, float conf = 0.9f)
    {
        var layout = new Layout
        {
            Confidence = conf,
            TextAnchor = new TextAnchor { TextSegments = { new TextSegment { StartIndex = seg.start, EndIndex = seg.start + seg.len } } }
        };
        if (l is { } x)
            layout.BoundingPoly = new BoundingPoly
            {
                NormalizedVertices =
                {
                    new NormalizedVertex { X = x, Y = t!.Value },
                    new NormalizedVertex { X = x + w!.Value, Y = t.Value + h!.Value }
                }
            };
        return layout;
    }

    private static DocumentAiMapper NewMapper() => new(new TextNormalizer());

    [Fact]
    public void Maps_form_fields_lines_and_tables_with_bbox_and_normalization()
    {
        var tb = new TextBuilder();
        var key = tb.Add("ยอดรวม");          // form field key
        var val = tb.Add("๑,๒๓๔.๕๖");        // form field value (Thai digits + thousands sep)
        var line = tb.Add("เลขที่ใบกำกับ");   // a plain line
        var hdr = tb.Add("Amount");          // table header cell
        var cell = tb.Add("๑๕.๐๐");          // table body cell (Thai digits)

        var page = new Page();
        page.FormFields.Add(new FormField { FieldName = Lay(key), FieldValue = Lay(val, 0.60f, 0.10f, 0.20f, 0.03f, 0.95f) });
        page.Lines.Add(new Line { Layout = Lay(line, 0.05f, 0.05f, 0.30f, 0.02f) });

        var table = new Table();
        table.HeaderRows.Add(new TableRow { Cells = { new TableCell { RowSpan = 1, ColSpan = 1, Layout = Lay(hdr, 0.70f, 0.40f, 0.20f, 0.03f) } } });
        table.BodyRows.Add(new TableRow { Cells = { new TableCell { RowSpan = 1, ColSpan = 1, Layout = Lay(cell, 0.70f, 0.45f, 0.20f, 0.03f) } } });
        page.Tables.Add(table);

        var doc = new GcpDocument { Text = tb.Text };
        doc.Pages.Add(page);

        var ex = NewMapper().Map(doc, "GOOGLE_DOCAI", "test");

        Assert.Equal("GOOGLE_DOCAI", ex.Engine);
        Assert.Equal(1, ex.PageCount);

        // KEY/VALUE block: "Key: Value", with a bbox and confidence
        var value = Assert.Single(ex.TextBlocks, b => b.BlockType == "VALUE");
        Assert.Equal("ยอดรวม: ๑,๒๓๔.๕๖", value.Content);
        Assert.Equal("1234.56", value.NormalizedContent);     // Thai digits + sep -> decimal
        Assert.NotNull(value.BBoxLeft);
        Assert.Equal(0.95m, value.Confidence);

        // LINE block
        var lineBlock = Assert.Single(ex.TextBlocks, b => b.BlockType == "LINE");
        Assert.Equal("เลขที่ใบกำกับ", lineBlock.Content);
        Assert.NotNull(lineBlock.BBoxLeft);

        // table -> cells with geometry + normalization
        var t = Assert.Single(ex.Tables);
        Assert.Equal(2, t.Cells.Count);
        var header = Assert.Single(t.Cells, c => c.IsHeader);
        Assert.Equal("Amount", header.Content);
        var body = Assert.Single(t.Cells, c => !c.IsHeader);
        Assert.Equal("๑๕.๐๐", body.Content);
        Assert.Equal("15.00", body.NormalizedContent);        // Thai digits -> decimal
        Assert.NotNull(body.BBoxLeft);
    }

    [Fact]
    public void MapInto_offsets_page_numbers_so_batch_shards_merge()
    {
        var mapper = NewMapper();
        var ex = new OcrPipeline.Web.Domain.OcrExtraction { Engine = "GOOGLE_DOCAI" };

        int offset = 0;
        offset += mapper.MapInto(ex, OneLineDoc("first page line"), offset);   // page 1
        offset += mapper.MapInto(ex, OneLineDoc("second page line"), offset);  // page 2
        ex.PageCount = offset;
        mapper.Normalize(ex);

        Assert.Equal(2, ex.PageCount);
        Assert.Contains(ex.TextBlocks, b => b.PageNumber == 1 && b.Content == "first page line");
        Assert.Contains(ex.TextBlocks, b => b.PageNumber == 2 && b.Content == "second page line");
    }

    private static GcpDocument OneLineDoc(string text)
    {
        var page = new Page();
        page.Lines.Add(new Line { Layout = Lay((0, text.Length)) });
        var doc = new GcpDocument { Text = text };
        doc.Pages.Add(page);
        return doc;
    }
}
