using System.Text.Json;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// ACCEPTANCE: end-to-end zonal TABLE extraction against the real east-repair sample. Draws one
/// line_item table zone over the three body rows with columns description / qty(anchor,INT) /
/// unit_price(DECIMAL) / amount(DECIMAL), then asserts the three rows match ground truth.
/// SELF-SKIPPING where tessdata/native isn't present (bare CI); runs for real on a dev machine.
/// Table-zone coords are hand-measured against samples/east-repair-invoice.png (750x1061) — if a
/// row is missed, re-measure the zone/columns in the designer (a measurement issue, not logic).
/// </summary>
public sealed class ZonalTableIntegrationTests
{
    [Fact]
    public async Task Zonal_table_extracts_three_line_items_from_the_sample_invoice()
    {
        string? tessdata = FindUp("tessdata", isDir: true) is { } d && File.Exists(Path.Combine(d, "eng.traineddata")) ? d : null;
        string? sample = FindUp(Path.Combine("samples", "east-repair-invoice.png"), isDir: false);
        if (tessdata is null || sample is null) return; // skip where the environment lacks tessdata/sample

        var opts = Options.Create(new TesseractOptions { TessdataPath = tessdata, Languages = "eng", Dpi = 300, MinOcrWidth = 2200 });
        var preprocessor = new ImagePreprocessor();
        var normalizer = new TextNormalizer();
        var tesseract = new TesseractOcrEngine(opts, preprocessor, normalizer);
        var engine = new MappingEngine(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), normalizer);
        var svc = new ZonalExtractionService(tesseract, preprocessor, new PagePreviewRenderer(), engine, normalizer, opts);

        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "InvoiceModel", MappingMode = "ZONAL",
            Fields = { new MappingField
            {
                FieldId = 10, TargetProperty = "LineItems", DataType = "STRING", SourceType = "TABLE_CELL",
                MinConfidence = 0.40m, ZonePage = 1,
                ZoneX = 0.073m, ZoneY = 0.386m, ZoneW = 0.854m, ZoneH = 0.110m  // body rows only (excludes header)
            } }
        };
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [10] = new()
            {
                Col("description", "STRING",  0.155m, 0.565m, sort: 0),
                Col("qty",         "INT",     0.073m, 0.155m, anchor: true, sort: 1),
                Col("unit_price",  "DECIMAL", 0.565m, 0.755m, sort: 2),
                Col("amount",      "DECIMAL", 0.755m, 0.927m, sort: 3),
            }
        };

        var doc = new Document { DocumentId = 1, StoredPath = sample, ContentType = "image/png", OcrLanguages = "eng" };

        MappingOutcome outcome;
        try
        {
            outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        }
        catch (System.Exception ex) when (ex is System.DllNotFoundException or System.BadImageFormatException)
        {
            return; // native libtesseract not loadable here -> skip
        }

        var mv = outcome.Values.Single(v => v.TargetProperty == "LineItems");
        Assert.NotNull(mv.NormalizedValue);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(mv.NormalizedValue!)!;

        Assert.True(3 == rows.Count, "rows=" + rows.Count + " json=" + mv.NormalizedValue);

        AssertRow(rows[0], "brake", 1, 100.00m, 100.00m);
        AssertRow(rows[1], "pedal", 2, 15.00m, 30.00m);
        AssertRow(rows[2], "abor",  3, 5.00m, 15.00m);   // "Labor" (case-insensitive substring)
    }

    private static void AssertRow(Dictionary<string, JsonElement> row, string descNeedle, long qty, decimal unit, decimal amount)
    {
        Assert.Contains(descNeedle, row["description"].GetString() ?? "", System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(qty, row["qty"].GetInt64());
        Assert.Equal(unit, row["unit_price"].GetDecimal());
        Assert.Equal(amount, row["amount"].GetDecimal());
    }

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true };

    private static string? FindUp(string relative, bool isDir)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (isDir ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
