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
/// Offline test of the table assembly (no images/OCR/DB): drive ZonalExtractionService.BuildTableRowsAsync
/// with synthetic anchor word boxes and a canned per-cell reader. Verifies row segmentation,
/// multi-line-cell collapse (LineSelect) and typed values, all through the real MappingEngine path.
/// </summary>
public sealed class ZonalTableBuildTests
{
    private static ZonalExtractionService NewService()
    {
        var engine = new MappingEngine(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), new TextNormalizer());
        return new ZonalExtractionService(null!, null!, null!, engine, new TextNormalizer(), Options.Create(new TesseractOptions()));
    }

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true };

    [Fact]
    public async Task Builds_three_typed_rows_with_multiline_description_collapsed()
    {
        // zone is the whole image (0..1); qty is the anchor column at x in [0.50, 0.65]
        var tableField = new MappingField
        {
            FieldId = 10, TargetProperty = "LineItems", SourceType = "TABLE_CELL",
            ZoneX = 0m, ZoneY = 0m, ZoneW = 1m, ZoneH = 1m
        };
        var cols = new List<MappingTableColumn>
        {
            Col("description", "STRING",  0.00m, 0.50m, sort: 0),
            Col("qty",         "INT",     0.50m, 0.65m, anchor: true, sort: 1),
            Col("unit_price",  "DECIMAL", 0.65m, 0.82m, sort: 2),
            Col("amount",      "DECIMAL", 0.82m, 1.00m, sort: 3),
        };

        // anchor (qty) words at three baselines -> three rows
        var words = new List<WordBox>
        {
            new("Front", 0.05, 0.10, 0.20, 0.04, 0.9m), new("1", 0.55, 0.10, 0.03, 0.04, 0.9m),
            new("New",   0.05, 0.35, 0.20, 0.04, 0.9m), new("2", 0.55, 0.35, 0.03, 0.04, 0.9m),
            new("Labor", 0.05, 0.60, 0.20, 0.04, 0.9m), new("3", 0.55, 0.60, 0.03, 0.04, 0.9m),
        };

        var cells = new Dictionary<(int, string), string>
        {
            [(0, "description")] = "Front and rear\nbrake cables", [(0, "qty")] = "1", [(0, "unit_price")] = "100.00", [(0, "amount")] = "100.00",
            [(1, "description")] = "New set of pedal arms",        [(1, "qty")] = "2", [(1, "unit_price")] = "15.00",  [(1, "amount")] = "30.00",
            [(2, "description")] = "Labor 3hrs",                   [(2, "qty")] = "3", [(2, "unit_price")] = "5.00",   [(2, "amount")] = "15.00",
        };

        var result = await NewService().BuildTableRowsAsync(
            tableField, cols, words, inkProfile: null,
            readCell: (r, col, _) => Task.FromResult((cells[(r, col.TargetSubProperty)], 0.9m)));

        Assert.Equal(3, result.Rows.Count);

        // multi-line description collapsed with a space (LineSelectMode default ALL)
        Assert.Equal("Front and rear brake cables", result.Rows[0]["description"]);
        Assert.Equal(1L, result.Rows[0]["qty"]);        // INT -> long
        Assert.Equal(100.00m, result.Rows[0]["amount"]); // DECIMAL -> decimal

        Assert.Equal("New set of pedal arms", result.Rows[1]["description"]);
        Assert.Equal(2L, result.Rows[1]["qty"]);
        Assert.Equal(30.00m, result.Rows[1]["amount"]);

        Assert.Equal(3L, result.Rows[2]["qty"]);
        Assert.Equal(15.00m, result.Rows[2]["amount"]);
        Assert.NotNull(result.Conf);
    }
}
