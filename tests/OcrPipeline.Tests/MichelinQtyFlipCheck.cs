using System.Net.Http;
using System.Text;
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
/// DIAGNOSTIC (not a strict gate): extracts the line_item QUANTITY for every Michelin row on pages 1-3
/// through the real zonal path with the CORRECTED columns, so we can diff textline-orientation ON vs OFF.
/// The qty cells are tiny single-digit crops where use_textline_orientation can silently flip a digit
/// 180° (9->6). Dumps page/row qty + articlecode + unitprice to $DOCRT_OUT\michelin_qty.txt and flags any
/// qty that mismatches the document ground truth (top-to-bottom). Self-skips if sample/sidecar absent.
/// </summary>
public sealed class MichelinQtyFlipCheck
{
    // document ground-truth qty sequences (top-to-bottom), pages 1..3
    private static readonly Dictionary<int, int[]> GtQty = new()
    {
        [1] = new[] { 6, 14, 9 },
        [2] = new[] { 57, 99, 2, 31, 6, 7 },
        [3] = new[] { 11, 14, 1, 1, 12 },
    };

    [Fact]
    public async Task Michelin_quantities_per_page_through_zonal_path()
    {
        string? sample = FindUp(Path.Combine("samples", "michelin-invoice.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return;

        var opts = Options.Create(new TesseractOptions { TessdataPath = "", Languages = "eng", Dpi = 300, MinOcrWidth = 2200 });
        var preprocessor = new ImagePreprocessor();
        var normalizer = new TextNormalizer();
        var tess = new TesseractOcrEngine(opts, preprocessor, normalizer);
        var paddle = new PaddleRegionOcrEngine(new SimpleHttpClientFactory(), tess,
            Options.Create(new PaddleOptions { BaseUrl = "http://localhost:8080", TimeoutSeconds = 120 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaddleRegionOcrEngine>.Instance);
        var engine = new MappingEngine(new TransformerPipeline(Array.Empty<IValueTransformer>()), normalizer);
        var svc = new ZonalExtractionService(paddle, preprocessor, new PagePreviewRenderer(), engine, normalizer, opts,
            Options.Create(new LineItemConsolidationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ZonalExtractionService>.Instance);

        var sb = new StringBuilder();
        sb.AppendLine("Michelin line_item QUANTITY per page — real zonal path, corrected columns");
        sb.AppendLine($"engine sidecar=:8080   (textline state = whatever the running container was built with)");
        sb.AppendLine(new string('-', 78));

        int totalMismatch = 0;
        var doc = new Document { DocumentId = 65, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "eng" };

        foreach (var page in new[] { 1, 2, 3 })
        {
            var template = new MappingTemplate { TemplateId = 99, TargetModel = "Michelin", MappingMode = "ZONAL" };
            template.Fields.Add(new MappingField
            {
                FieldId = 1, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
                MinConfidence = 0.30m, ZonePage = page, ZoneX = 0.03m, ZoneY = 0.41m, ZoneW = 0.95m, ZoneH = 0.55m
            });
            var columns = new Dictionary<int, List<MappingTableColumn>>
            {
                [1] = new()
                {
                    Col("quantity",    "STRING",  0.03m, 0.10m, anchor: true, sort: 0),
                    Col("uom",         "STRING",  0.10m, 0.17m, sort: 1),
                    Col("description", "STRING",  0.17m, 0.61m, sort: 2),
                    Col("articlecode", "STRING",  0.61m, 0.75m, sort: 3),
                    Col("unitprice",   "STRING",  0.75m, 0.87m, sort: 4),
                    Col("total",       "STRING",  0.87m, 0.98m, sort: 5),
                }
            };

            var outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
            var tv = outcome.Values.Single(v => v.TargetProperty == "line_item");
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(tv.NormalizedValue ?? "[]")!;

            int[] gt = GtQty[page];
            sb.AppendLine($"PAGE {page}   GT qty (top->bottom): [{string.Join(", ", gt)}]   extracted rows: {rows.Count}");
            for (int i = 0; i < rows.Count; i++)
            {
                string qty = Cell(rows[i], "quantity");
                string art = Cell(rows[i], "articlecode");
                string up = Cell(rows[i], "unitprice");
                string gtq = i < gt.Length ? gt[i].ToString() : "?";
                bool ok = qty.Trim() == gtq;
                if (!ok && i < gt.Length) totalMismatch++;
                sb.AppendLine($"  row{i}: qty='{qty}'  (gt={gtq})  {(ok ? "ok" : "<<< MISMATCH")}   article={art}  unit={up}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(new string('-', 78));
        sb.AppendLine($"TOTAL QTY MISMATCHES (positional, pages 1-3): {totalMismatch}");

        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_qty.txt"), sb.ToString());
        // diagnostic: never fails the run, the report is the artifact we compare before/after.
    }

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true };

    private static string Cell(Dictionary<string, JsonElement> r, string k)
        => r.TryGetValue(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

    private static async Task<bool> SidecarUp()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; return (await h.GetAsync("http://localhost:8080/health")).IsSuccessStatusCode; }
        catch { return false; }
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        { string c = Path.Combine(dir.FullName, relative); if (File.Exists(c)) return c; }
        return null;
    }
}
