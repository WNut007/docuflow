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
/// Option-A consolidator tests over the REAL doc-75 (samples/michelin-invoice.pdf) shape: 14 genuine
/// line items + a qty-bearing "270 PC FCA FACTORY ... CIP PLACE OF DELIVERY" subtotal that the anchor
/// segmenter would otherwise emit as a phantom 15th item. Descriptions carry absorbed shipping metadata
/// (AVIEXP / Container Number / Our Reference / Your Order / Origin / Michelin Brand boilerplate).
/// Pure (no images/OCR/DB) like the segmenter tests; ground truth is the actual Paddle capture.
/// </summary>
public sealed class LineItemConsolidatorTests
{
    // Ground truth: the 14 item quantities from doc 75 (Paddle), in document order. Their sum is 270,
    // which is exactly what the subtotal row carries — the corroboration signal that must NOT, on its
    // own, drop a row.
    private static readonly long[] ItemQtys = { 6, 14, 9, 57, 99, 2, 31, 6, 7, 11, 14, 1, 1, 12 };

    private const double AnchorXs = 0.04, AnchorXe = 0.12;   // narrow left qty column

    // -------- pure unit-level: SelectItemRows / CleanDescriptionLines --------

    [Fact]
    public void SelectItemRows_drops_the_qty_bearing_subtotal_keeping_14_items()
    {
        var (bands, words) = BuildDoc75Geometry();
        Assert.Equal(15, bands.Count);   // sanity: segmenter-equivalent yields 14 items + the subtotal

        var sel = LineItemConsolidator.SelectItemRows(bands, words, AnchorXs, AnchorXe);

        Assert.Equal(14, sel.Kept.Count);
        var drop = Assert.Single(sel.Dropped);
        Assert.True(drop.FooterKeyword);             // PRIMARY reason: "FCA FACTORY" / "CIP PLACE OF DELIVERY"
        Assert.Equal(270L, drop.Qty);
        Assert.True(drop.QtyEqualsRunningSum);        // 270 == sum of the 14 kept qtys — corroboration only
    }

    [Fact]
    public void SelectItemRows_does_not_drop_a_legit_last_row_whose_qty_equals_the_running_sum()
    {
        // items qty 1 and 2, then a genuine last item qty 3 (== 1+2) with NO footer keyword. The
        // qty-equals-sum coincidence must not eat it — only a footer keyword drops a row.
        var words = new List<WordBox>
        {
            Anchor("1", 0.10), Text("Brake cable", 0.10),
            Anchor("2", 0.30), Text("Pedal arms", 0.30),
            Anchor("3", 0.50), Text("Labor 3hrs", 0.50),
        };
        var bands = new List<RowBand> { new(0.0, 0.2), new(0.2, 0.4), new(0.4, 1.0) };

        var sel = LineItemConsolidator.SelectItemRows(bands, words, AnchorXs, AnchorXe);

        Assert.Equal(3, sel.Kept.Count);     // nothing dropped
        Assert.Empty(sel.Dropped);
    }

    [Fact]
    public void CleanDescriptionLines_keeps_the_spec_line_and_drops_shipping_metadata()
    {
        var lines = new[]
        {
            "AVIEXP No/Warehouse: DA0051663374",
            "Container Number: CMAU472280",
            "Our Reference: 15821321 S4",
            "Your Order: USA104444A",
            "Michelin Brand",
            "Passenger Car Radial Tyre",
            "Origin : United States",
            "245/45 ZR18 (100Y) XL TL PILOT SPORT 4 S MI",
        };

        var cleaned = LineItemConsolidator.CleanDescriptionLines(lines);

        Assert.Equal(new[] { "245/45 ZR18 (100Y) XL TL PILOT SPORT 4 S MI" }, cleaned);
    }

    // -------- end-to-end through BuildTableRowsAsync (the real assembly path) --------

    [Fact]
    public async Task BuildTableRows_yields_exactly_14_clean_items_dropping_the_270_subtotal()
    {
        var tableField = new MappingField
        {
            FieldId = 34, TargetProperty = "line_item", SourceType = "TABLE_CELL",
            ZoneX = 0m, ZoneY = 0m, ZoneW = 1m, ZoneH = 1m
        };
        var cols = new List<MappingTableColumn>
        {
            Col("quantity",     "INT",     0.04m, 0.12m, anchor: true, sort: 0),
            Col("description",  "STRING",  0.12m, 0.60m, sort: 1),
            Col("unitprice",    "DECIMAL", 0.74m, 0.87m, sort: 2),
            Col("total amount", "DECIMAL", 0.87m, 1.00m, sort: 3),
        };

        var (bands, words) = BuildDoc75Geometry();

        // Canned per-cell reader keyed by the band's y-center -> the row index in doc order (0..14).
        // Description cells carry the absorbed metadata that (b) must strip.
        var result = await NewService().BuildTableRowsAsync(
            tableField, cols, words, inkProfile: null,
            readCell: (_, col, band) =>
            {
                int idx = RowIndexForBand(band);
                return Task.FromResult((CellText(idx, col.TargetSubProperty), 0.99m));
            },
            consolidate: new ConsolidateOptions());   // template gated IN -> consolidation runs

        Assert.Equal(14, result.Rows.Count);   // the "270" subtotal is gone — no phantom 15th row

        // qtys match ground truth, in order
        for (int i = 0; i < ItemQtys.Length; i++)
            Assert.Equal(ItemQtys[i], result.Rows[i]["quantity"]);

        // descriptions are the clean spec lines — no AVIEXP / Container / Our Reference / Origin / boilerplate
        Assert.Equal("245/45 ZR18 (100Y) XL TL PILOT SPORT 4 S MI", result.Rows[0]["description"]);
        Assert.Equal("T285/60R18 118/115S TL ALL-TERRAIN T/A KO2 LRD RWL", result.Rows[3]["description"]);
        foreach (var row in result.Rows)
        {
            var desc = (string)row["description"]!;
            Assert.DoesNotContain("AVIEXP", desc);
            Assert.DoesNotContain("Container Number", desc);
            Assert.DoesNotContain("Our Reference", desc);
            Assert.DoesNotContain("Origin", desc);
            Assert.DoesNotContain("Passenger Car Radial Tyre", desc);
        }

        // typed numerics survive for a spot-checked row (item 1)
        Assert.Equal(76.81m, result.Rows[0]["unitprice"]);
        Assert.Equal(460.86m, result.Rows[0]["total amount"]);
    }

    [Fact]
    public async Task BuildTableRows_without_consolidation_keeps_all_15_rows_and_raw_descriptions()
    {
        // The GATE: a template NOT opted in (consolidate == null, the default) must get the unchanged
        // path — the "270" subtotal stays as a 15th row and shipping metadata is NOT stripped. This is
        // what protects every non-Michelin template from the layout-specific lexicon.
        var tableField = new MappingField
        {
            FieldId = 34, TargetProperty = "line_item", SourceType = "TABLE_CELL",
            ZoneX = 0m, ZoneY = 0m, ZoneW = 1m, ZoneH = 1m
        };
        var cols = new List<MappingTableColumn>
        {
            Col("quantity",     "INT",     0.04m, 0.12m, anchor: true, sort: 0),
            Col("description",  "STRING",  0.12m, 0.60m, sort: 1),
        };

        var (_, words) = BuildDoc75Geometry();

        var result = await NewService().BuildTableRowsAsync(
            tableField, cols, words, inkProfile: null,
            readCell: (_, col, band) =>
            {
                int idx = RowIndexForBand(band);
                return Task.FromResult((CellText(idx, col.TargetSubProperty), 0.99m));
            });   // consolidate omitted -> null -> gate closed

        Assert.Equal(15, result.Rows.Count);                 // subtotal NOT dropped
        Assert.Equal(270L, result.Rows[14]["quantity"]);     // the phantom 15th row survives
        Assert.Contains("AVIEXP", (string)result.Rows[0]["description"]!);   // metadata NOT stripped
    }

    // -------- fixture: the doc-75 geometry (14 item rows + the subtotal) --------

    /// <summary>Build 15 anchor bands (14 items + the footer/subtotal) with their qty + footer words,
    /// laid out as evenly-spaced baselines. Each band's y-center encodes its doc-order index.</summary>
    private static (List<RowBand> Bands, List<WordBox> Words) BuildDoc75Geometry()
    {
        var bands = new List<RowBand>();
        var words = new List<WordBox>();
        int n = ItemQtys.Length;            // 14 items
        double step = 1.0 / (n + 1);        // +1 for the subtotal band

        for (int i = 0; i < n; i++)
        {
            double y = (i + 0.5) * step;
            bands.Add(new RowBand(i * step, (i + 1) * step));
            words.Add(Anchor(ItemQtys[i].ToString(), y));
            words.Add(Text("spec", y));     // some description ink in the band (text content comes from the reader)
        }

        // the qty-bearing subtotal: "270 ... FCA FACTORY ... CIP PLACE OF DELIVERY"
        double ys = (n + 0.5) * step;
        bands.Add(new RowBand(n * step, 1.0));
        words.Add(Anchor("270", ys));
        words.Add(Text("FCA FACTORY Freight Insurance Inland CIP PLACE OF DELIVERY", ys));

        return (bands, words);
    }

    private static int RowIndexForBand(RowBand band)
    {
        int n = ItemQtys.Length;
        double step = 1.0 / (n + 1);
        double yc = (band.YStart + band.YEnd) / 2.0;
        return (int)System.Math.Floor(yc / step);
    }

    private static string CellText(int idx, string sub) => sub switch
    {
        "quantity" => idx < ItemQtys.Length ? ItemQtys[idx].ToString() : "270",
        "unitprice" => idx == 0 ? "76.81" : "100.00",
        "total amount" => idx == 0 ? "460.86" : "100.00",
        "description" => idx switch
        {
            0 => "AVIEXP No/Warehouse: DA0051663374\nContainer Number: CMAU472280\nOur Reference: 15821321 S4\n"
                 + "Your Order: USA104444A\nMichelin Brand\nPassenger Car Radial Tyre\nOrigin : United States\n"
                 + "245/45 ZR18 (100Y) XL TL PILOT SPORT 4 S MI",
            3 => "B.F. Goodrich Light Truck Tyre\nOrigin : United States\n"
                 + "T285/60R18 118/115S TL ALL-TERRAIN T/A KO2 LRD RWL\nOur Reference: 15821324 S4",
            _ => "Origin : United States\n245/45 ZR18 (100Y) XL TL PILOT SPORT 4 S MI",
        },
        _ => "",
    };

    // -------- helpers --------

    private static WordBox Anchor(string text, double y) => new(text, 0.07, y, 0.03, 0.03, 0.99m);
    private static WordBox Text(string text, double y) => new(text, 0.20, y, 0.30, 0.03, 0.99m);

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true };

    private static ZonalExtractionService NewService()
    {
        var engine = new MappingEngine(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), new TextNormalizer());
        return new ZonalExtractionService(null!, null!, null!, engine, new TextNormalizer(), Options.Create(new TesseractOptions()),
            Options.Create(new LineItemConsolidationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ZonalExtractionService>.Instance);
    }
}
