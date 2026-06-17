using System.Text.Json;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline test of the Phase-3 multi-page orchestration (no images/OCR/DB): drive
/// ZonalExtractionService.BuildMultiPageAsync with canned per-page scalar/table readers and assert
/// header-once / totals-once placement, per-page table-region selection (with LAST→CONTINUATION
/// fallback), the single-page FIRST+LAST case, and cross-page row concatenation — all through the
/// real MappingEngine.RunZonalAsync path.
/// </summary>
public sealed class MultiPageBuildTests
{
    private static ZonalExtractionService NewService()
    {
        var engine = new MappingEngine(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), new TextNormalizer());
        // image/OCR deps are unused on the delegate-seam path -> null! is fine (mirrors ZonalTableBuildTests).
        return new ZonalExtractionService(null!, null!, null!, engine, new TextNormalizer(), Options.Create(new TesseractOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ZonalExtractionService>.Instance);
    }

    private static MappingField Scalar(int id, string prop, string role)
        => new() { FieldId = id, TargetProperty = prop, DataType = "STRING", SourceType = "KEY_VALUE",
                   ZonePageRole = role, ZoneX = 0.1m, ZoneY = 0.1m, ZoneW = 0.2m, ZoneH = 0.05m };

    private static MappingField Table(int id, string role)
        => new() { FieldId = id, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
                   ZonePageRole = role, ZoneX = 0m, ZoneY = 0.3m, ZoneW = 1m, ZoneH = 0.4m, MinConfidence = 0.3m };

    private static List<MappingTableColumn> Cols() => new()
    {
        new() { TargetSubProperty = "qty", DataType = "INT", IsAnchor = true, SortOrder = 0, IsActive = true },
        new() { TargetSubProperty = "description", DataType = "STRING", SortOrder = 1, IsActive = true },
    };

    private static ZonalExtractionService.TableResult PageRows(int pageNo)
        => new(new List<Dictionary<string, object?>> { new() { ["qty"] = (long)pageNo, ["description"] = "p" + pageNo } }, 0.9m);

    private static MappingTemplate AllRoles() => new()
    {
        TemplateId = 1, TargetModel = "M", MappingMode = "ZONAL",
        Fields = { Scalar(1, "invoice_no", "FIRST"), Scalar(2, "total", "LAST"),
                   Table(10, "FIRST"), Table(11, "CONTINUATION"), Table(12, "LAST") }
    };

    private static Dictionary<int, List<MappingTableColumn>> AllCols()
        => new() { [10] = Cols(), [11] = Cols(), [12] = Cols() };

    [Fact]
    public async Task Reads_header_once_totals_once_and_concatenates_rows_in_page_order()
    {
        var scalarCalls = new List<(string prop, int page)>();
        var tablePages = new List<(int page, int field)>();

        var outcome = await NewService().BuildMultiPageAsync(AllRoles(), totalPages: 3,
            ocrScalarOnPage: (f, page) => { scalarCalls.Add((f.TargetProperty, page)); return Task.FromResult((f.TargetProperty == "total" ? "100.00" : "INV-1", 0.9m)); },
            ocrTableOnPage: (f, _, page) => { tablePages.Add((page, f.FieldId)); return Task.FromResult(PageRows(page)); },
            steps: new Dictionary<int, List<TransformerStep>>(),
            columnsByField: AllCols());

        Assert.Equal(new[] { ("invoice_no", 1), ("total", 3) }, scalarCalls.OrderBy(c => c.page).ToArray());
        Assert.Equal(new[] { (1, 10), (2, 11), (3, 12) }, tablePages.ToArray());

        var li = outcome.Values.Single(v => v.TargetProperty == "line_item");
        using var doc = JsonDocument.Parse(li.NormalizedValue!);
        Assert.Equal(new[] { "p1", "p2", "p3" },
            doc.RootElement.EnumerateArray().Select(r => r.GetProperty("description").GetString()).ToArray());
    }

    [Fact]
    public async Task Single_page_uses_first_region_and_reads_last_totals_on_that_page()
    {
        var scalarCalls = new List<(string prop, int page)>();
        var tablePages = new List<(int page, int field)>();

        var outcome = await NewService().BuildMultiPageAsync(AllRoles(), totalPages: 1,
            ocrScalarOnPage: (f, page) => { scalarCalls.Add((f.TargetProperty, page)); return Task.FromResult(("x", 0.9m)); },
            ocrTableOnPage: (f, _, page) => { tablePages.Add((page, f.FieldId)); return Task.FromResult(PageRows(page)); },
            steps: new Dictionary<int, List<TransformerStep>>(),
            columnsByField: AllCols());

        Assert.Contains(("invoice_no", 1), scalarCalls);
        Assert.Contains(("total", 1), scalarCalls);          // LAST totals read on the single page
        Assert.Equal(new[] { (1, 10) }, tablePages.ToArray()); // only FIRST region, no double-read

        var li = outcome.Values.Single(v => v.TargetProperty == "line_item");
        using var doc = JsonDocument.Parse(li.NormalizedValue!);
        Assert.Single(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Last_page_falls_back_to_continuation_region_when_no_last_drawn()
    {
        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "M", MappingMode = "ZONAL",
            Fields = { Table(10, "FIRST"), Table(11, "CONTINUATION") }   // no LAST region
        };
        var tablePages = new List<(int page, int field)>();

        await NewService().BuildMultiPageAsync(template, totalPages: 3,
            ocrScalarOnPage: (f, page) => Task.FromResult(("x", 0.9m)),
            ocrTableOnPage: (f, _, page) => { tablePages.Add((page, f.FieldId)); return Task.FromResult(PageRows(page)); },
            steps: new Dictionary<int, List<TransformerStep>>(),
            columnsByField: new Dictionary<int, List<MappingTableColumn>> { [10] = Cols(), [11] = Cols() });

        Assert.Equal(new[] { (1, 10), (2, 11), (3, 11) }, tablePages.ToArray());  // p3 (LAST) reuses CONTINUATION
    }

    [Fact]
    public async Task Continuation_rows_type_by_first_structure_even_when_region_columns_drift()
    {
        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "M", MappingMode = "ZONAL",
            Fields = { Table(10, "FIRST"), Table(11, "CONTINUATION") }
        };
        // FIRST is canonical: qty INT. CONTINUATION drifted to qty STRING (e.g. hand-edited after a copy).
        var first = new List<MappingTableColumn>
        {
            new() { TargetSubProperty = "qty", DataType = "INT", IsAnchor = true, SortOrder = 0, IsActive = true },
            new() { TargetSubProperty = "description", DataType = "STRING", SortOrder = 1, IsActive = true },
        };
        var cont = new List<MappingTableColumn>
        {
            new() { TargetSubProperty = "qty", DataType = "STRING", IsAnchor = true, SortOrder = 0, IsActive = true },  // DRIFT
            new() { TargetSubProperty = "description", DataType = "STRING", SortOrder = 1, IsActive = true },
        };
        var cols = new Dictionary<int, List<MappingTableColumn>> { [10] = first, [11] = cont };

        var outcome = await NewService().BuildMultiPageAsync(template, totalPages: 3,
            ocrScalarOnPage: (f, page) => Task.FromResult(("x", 0.9m)),
            // type each cell by the DataType of the column actually passed in -> reveals which structure won
            ocrTableOnPage: (f, c, page) =>
            {
                var row = new Dictionary<string, object?>();
                foreach (var col in c) row[col.TargetSubProperty] = col.DataType == "INT" ? (object)(long)page : col.TargetSubProperty + page;
                return Task.FromResult(new ZonalExtractionService.TableResult(new List<Dictionary<string, object?>> { row }, 0.9m));
            },
            steps: new Dictionary<int, List<TransformerStep>>(),
            columnsByField: cols);

        var li = outcome.Values.Single(v => v.TargetProperty == "line_item");
        using var doc = JsonDocument.Parse(li.NormalizedValue!);
        var rows = doc.RootElement.EnumerateArray().ToArray();
        // page 2 came from the CONTINUATION region, but qty is a NUMBER (typed INT per FIRST's structure),
        // not the drifted STRING the region itself declared.
        Assert.Equal(JsonValueKind.Number, rows[1].GetProperty("qty").ValueKind);
        Assert.Equal(2, rows[1].GetProperty("qty").GetInt64());
    }
}
