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
/// Part-1 validation (description cleanup): runs the REAL zonal path over Michelin pages 1-3 with the
/// corrected template-12 columns, extracting the description twice — LineSelectMode=ALL (cluttered) vs
/// ANCHOR (spec-only) — and asserts every ANCHOR description carries NO metadata token. Plus a
/// doc_rt spot-check that ANCHOR on a single-line Thai description is unchanged. Eyeball the dumped
/// table against the real specs; self-skips if sample/sidecar absent.
/// </summary>
public sealed class MichelinDescriptionCheck
{
    // tokens that must NOT appear in a clean (spec-only) description
    private static readonly string[] MetadataTokens =
    {
        "Goodrich", "Michelin Brand", "Our Reference", "Your Order", "Origin", "Passenger",
        "Light Truck", "Radial Tyre", "AVIEXP", "Container", "Seal", "UC Number", "Warehouse", "USA1"
    };

    [Fact]
    public async Task Michelin_description_anchor_mode_drops_metadata()
    {
        string? sample = FindUp(Path.Combine("samples", "michelin-invoice.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return;

        var (svc, _) = BuildSvc();
        var doc = new Document { DocumentId = 65, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "eng" };

        var sb = new StringBuilder();
        sb.AppendLine("Part-1: Michelin description  ALL (before)  vs  ANCHOR (after)  — real zonal path");
        sb.AppendLine(new string('-', 110));

        int leaks = 0, rowsChecked = 0;
        foreach (var page in new[] { 1, 2, 3 })
        {
            var allRows = await ExtractDescriptions(svc, doc, page, "ALL");
            var anchorRows = await ExtractDescriptions(svc, doc, page, "ANCHOR");
            sb.AppendLine($"PAGE {page}");
            int n = Math.Max(allRows.Count, anchorRows.Count);
            for (int i = 0; i < n; i++)
            {
                string qty = i < anchorRows.Count ? anchorRows[i].qty : (i < allRows.Count ? allRows[i].qty : "?");
                string before = i < allRows.Count ? allRows[i].desc : "";
                string after = i < anchorRows.Count ? anchorRows[i].desc : "";
                bool leak = MetadataTokens.Any(t => after.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (i < anchorRows.Count) { rowsChecked++; if (leak) leaks++; }
                sb.AppendLine($"  qty={qty,-3}");
                sb.AppendLine($"     before(ALL):    {Trunc(before, 90)}");
                sb.AppendLine($"     after(ANCHOR):  {Trunc(after, 90)}   {(leak ? "<<< METADATA LEAK" : "clean")}");
            }
            sb.AppendLine();
        }
        sb.AppendLine(new string('-', 110));
        sb.AppendLine($"rows checked={rowsChecked}   ANCHOR metadata leaks={leaks}");

        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_description.txt"), sb.ToString());

        Assert.True(leaks == 0, $"\n{sb}\n{leaks} ANCHOR descriptions still contain metadata tokens");
    }

    [Fact]
    public async Task DocRt_description_anchor_mode_unchanged()
    {
        string? sample = FindUp(Path.Combine("samples", "doc_rt.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return;

        var (svc, _) = BuildSvc("tha+eng");
        var doc = new Document { DocumentId = 1, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "tha+eng" };

        var template = new MappingTemplate { TemplateId = 99, TargetModel = "ThaiReceipt", MappingMode = "ZONAL" };
        template.Fields.Add(new MappingField
        {
            FieldId = 1, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
            MinConfidence = 0.40m, ZonePage = 1, ZoneX = 0.040m, ZoneY = 0.348m, ZoneW = 0.920m, ZoneH = 0.030m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] = new()
            {
                Col("description", "STRING",  0.040m, 0.590m, mode: "ANCHOR", sort: 0),
                Col("qty",         "DECIMAL", 0.590m, 0.655m, anchor: true, sort: 1),
                Col("unit_price",  "DECIMAL", 0.655m, 0.735m, sort: 2),
                Col("amount",      "DECIMAL", 0.845m, 0.960m, sort: 3),
            }
        };
        var outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        Assert.True(rows.Count >= 1, "no rows");
        string desc = Cell(rows[0], "description");
        Assert.Contains("ค่าบริการ", desc);  // ANCHOR leaves the single-line Thai description intact
    }

    // ---- helpers ----

    private async Task<List<(string qty, string desc)>> ExtractDescriptions(
        ZonalExtractionService svc, Document doc, int page, string descMode)
    {
        var template = new MappingTemplate { TemplateId = 99, TargetModel = "Michelin", MappingMode = "ZONAL" };
        template.Fields.Add(new MappingField
        {
            FieldId = 1, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
            MinConfidence = 0.30m, ZonePage = page, ZoneX = 0.03m, ZoneY = 0.45m, ZoneW = 0.95m, ZoneH = 0.51m  // post-USD-fix zone
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            // EXACT live template-12 column boundaries (as drawn in the designer), so this mirrors prod.
            [1] = new()
            {
                Col("quantity",    "STRING",  0.03m, 0.10m, anchor: true, sort: 0),
                Col("uom",         "STRING",  0.10m, 0.19m, sort: 1),
                Col("description", "STRING",  0.19m, 0.60m, mode: descMode, sort: 2),
                Col("articlecode", "STRING",  0.60m, 0.72m, sort: 3),
                Col("unitprice",   "STRING",  0.72m, 0.84m, sort: 4),
                Col("total",       "STRING",  0.84m, 0.97m, sort: 5),
            }
        };
        var outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        return rows.Select(r => (Cell(r, "quantity"), Cell(r, "description").Replace("\n", " ").Trim())).ToList();
    }

    private static (ZonalExtractionService svc, PaddleRegionOcrEngine paddle) BuildSvc(string langs = "eng")
    {
        var opts = Options.Create(new TesseractOptions { TessdataPath = "", Languages = langs, Dpi = 300, MinOcrWidth = 2200 });
        var pre = new ImagePreprocessor();
        var norm = new TextNormalizer();
        var tess = new TesseractOcrEngine(opts, pre, norm);
        var paddle = new PaddleRegionOcrEngine(new SimpleHttpClientFactory(), tess,
            Options.Create(new PaddleOptions { BaseUrl = "http://localhost:8080", TimeoutSeconds = 120 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaddleRegionOcrEngine>.Instance);
        var eng = new MappingEngine(new TransformerPipeline(Array.Empty<IValueTransformer>()), norm);
        var svc = new ZonalExtractionService(paddle, pre, new PagePreviewRenderer(), eng, norm, opts,
            Options.Create(new LineItemConsolidationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ZonalExtractionService>.Instance);
        return (svc, paddle);
    }

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0, string? mode = null)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true, LineSelectMode = mode };

    private static string Cell(Dictionary<string, JsonElement> r, string k)
        => r.TryGetValue(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

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
