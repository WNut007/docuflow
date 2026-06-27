using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// GATE #6 (Thai): drives the REAL ZonalExtractionService over samples/doc_rt.pdf through the v5
/// PaddleRegionOcrEngine (Thai PP-OCRv5 mobile det/rec) against the manually-drawn zones in
/// <see cref="Fields"/> — the same path the app uses with Ocr:RegionProvider=Paddle + manual columns
/// (no PP-Structure / ③). Zones were derived empirically from the full-page OCR boxes (DocRtMeasure)
/// so each covers exactly one locked ground-truth value on PAGE 1 (the clean original "ต้นฉบับ").
///
/// Emits a per-field table (raw read vs ground truth) to $DOCRT_OUT\doc_rt_validation.txt, THEN asserts.
/// Self-skips when the sample or the sidecar is unreachable (so it never silently green-passes).
/// </summary>
public sealed class DocRtThaiValidation
{
    private sealed record Spec(
        int Id, string Prop, string Expected, Match Mode,
        decimal X, decimal Y, decimal W, decimal H);

    private enum Match { Exact, Numeric, Contains }

    // page-1 zones (page-normalized 0..1), padded a hair around the measured OCR boxes.
    private static readonly Spec[] Fields =
    {
        // top edge dropped to 0.041 to sit BELOW the "(ต้นฉบับ)" original-stamp printed above the title tail.
        new(1,  "tax_invoice_title", "ใบกำกับภาษี",       Match.Contains, 0.465m, 0.041m, 0.495m, 0.041m),
        new(2,  "doc_number",        "RT-20260600038",   Match.Exact,    0.768m, 0.099m, 0.135m, 0.019m),
        new(3,  "issue_date",        "24/06/2026",       Match.Exact,    0.768m, 0.117m, 0.110m, 0.019m),
        new(4,  "reference",         "IVT-20260600176",  Match.Exact,    0.768m, 0.134m, 0.140m, 0.020m),
        new(5,  "seller_tax_id",     "0105565001098",    Match.Contains, 0.040m, 0.162m, 0.300m, 0.021m),
        new(6,  "customer_name",     "ธัญพืช",            Match.Contains, 0.117m, 0.203m, 0.140m, 0.020m),
        new(7,  "customer_address",  "เมืองนนทบุรีจังหวัดนนทบุรี", Match.Contains, 0.117m, 0.249m, 0.225m, 0.020m),
        new(8,  "customer_tax_id",   "0105541059576",    Match.Contains, 0.040m, 0.264m, 0.300m, 0.021m),
        new(9,  "taxable_amount",    "450.00",           Match.Numeric,  0.510m, 0.616m, 0.100m, 0.020m),
        new(10, "total_incl_vat",    "481.50",           Match.Numeric,  0.820m, 0.628m, 0.130m, 0.024m),
        new(11, "vat",               "31.50",            Match.Numeric,  0.518m, 0.634m, 0.095m, 0.020m),
        new(12, "withholding_tax",   "13.50",            Match.Numeric,  0.870m, 0.676m, 0.095m, 0.020m),
        new(13, "amount_paid",       "468.00",           Match.Numeric,  0.861m, 0.694m, 0.100m, 0.020m),
        new(14, "bank_account",      "118-3-65426-4",    Match.Contains, 0.430m, 0.746m, 0.200m, 0.020m),
    };

    [Fact]
    public async Task DocRt_thai_fields_read_through_real_zonal_path()
    {
        string? sample = FindUp(Path.Combine("samples", "doc_rt.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return; // sidecar not running -> skip rather than fail

        string? tessdata = FindUp(Path.Combine("tessdata")) is { } d && Directory.Exists(d) ? d : null;
        // PRODUCTION default MinOcrWidth=2200 (NOT a test override): with crop upscaling now engine-owned,
        // ZonalExtractionService hands PaddleRegionOcrEngine a NATIVE crop regardless of this value (only the
        // Tesseract fallback would enlarge it). So this validates the real production wiring — PP-OCRv5 gets
        // native crops because the engine wants them, not because a test knob disabled the upscale.
        var opts = Options.Create(new TesseractOptions
        {
            TessdataPath = tessdata ?? "", Languages = "tha+eng", Dpi = 300, MinOcrWidth = 2200
        });
        var preprocessor = new ImagePreprocessor();
        var normalizer = new TextNormalizer();
        var tesseractFallback = new TesseractOcrEngine(opts, preprocessor, normalizer);
        var paddle = new PaddleRegionOcrEngine(
            new SimpleHttpClientFactory(), tesseractFallback,
            Options.Create(new PaddleOptions { BaseUrl = "http://localhost:8080", TimeoutSeconds = 120 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaddleRegionOcrEngine>.Instance);
        var engine = new MappingEngine(new TransformerPipeline(Array.Empty<IValueTransformer>()), normalizer);
        var svc = new ZonalExtractionService(paddle, preprocessor, new PagePreviewRenderer(), engine, normalizer, opts,
            Options.Create(new LineItemConsolidationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ZonalExtractionService>.Instance);

        var template = new MappingTemplate { TemplateId = 1, TargetModel = "ThaiReceipt", MappingMode = "ZONAL" };
        foreach (var f in Fields)
            template.Fields.Add(new MappingField
            {
                FieldId = f.Id, TargetProperty = f.Prop, DataType = "STRING", SourceType = "KEY_VALUE",
                MinConfidence = 0.40m, ZonePage = 1, ZoneX = f.X, ZoneY = f.Y, ZoneW = f.W, ZoneH = f.H,
                ZoneOcrHint = "TEXT"
            });

        // line-item table (manual columns; no ③). Single data row; qty is the row anchor.
        const int tableId = 100;
        template.Fields.Add(new MappingField
        {
            FieldId = tableId, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
            MinConfidence = 0.40m, ZonePage = 1, ZoneX = 0.040m, ZoneY = 0.348m, ZoneW = 0.920m, ZoneH = 0.030m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [tableId] = new()
            {
                Col("description", "STRING",  0.040m, 0.590m, sort: 0),
                Col("qty",         "DECIMAL", 0.590m, 0.655m, anchor: true, sort: 1),
                Col("unit_price",  "DECIMAL", 0.655m, 0.735m, sort: 2),
                Col("amount",      "DECIMAL", 0.845m, 0.960m, sort: 3),
            }
        };

        var doc = new Document { DocumentId = 1, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "tha+eng" };

        MappingOutcome outcome = await svc.ProcessAsync(
            doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);

        // ---- build per-field report ------------------------------------------------
        var sb = new StringBuilder();
        sb.AppendLine("GATE #6 — Thai validation via real ZonalExtractionService + v5 PaddleRegionOcrEngine");
        sb.AppendLine("doc=samples/doc_rt.pdf page=1   engine=th_PP-OCRv5_mobile_rec (sidecar /ocr)");
        sb.AppendLine(new string('-', 96));
        sb.AppendLine($"{"field",-20}{"expected",-30}{"raw read",-32}{"conf",-7}result");
        sb.AppendLine(new string('-', 96));

        int pass = 0;
        var failures = new List<string>();
        foreach (var f in Fields)
        {
            var mv = outcome.Values.Single(v => v.TargetProperty == f.Prop);
            string raw = (mv.RawValue ?? "").Replace("\n", " ").Trim();
            bool ok = f.Mode switch
            {
                Match.Exact    => raw.Trim() == f.Expected,
                Match.Numeric  => NumEq(raw, f.Expected),
                Match.Contains => StripWs(raw).Contains(StripWs(f.Expected)),
                _ => false
            };
            if (ok) pass++; else failures.Add(f.Prop);
            sb.AppendLine($"{f.Prop,-20}{f.Expected,-30}{Trunc(raw, 30),-32}{mv.Confidence,-7:F2}{(ok ? "PASS" : "FAIL")}");
        }

        // ---- table row -------------------------------------------------------------
        var tv = outcome.Values.Single(v => v.TargetProperty == "line_item");
        sb.AppendLine(new string('-', 96));
        sb.AppendLine("line_item (manual columns, no ③):");
        sb.AppendLine(tv.NormalizedValue ?? "(null)");
        bool tableOk = false;
        try
        {
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(tv.NormalizedValue ?? "[]")!;
            if (rows.Count >= 1)
            {
                var r = rows[0];
                string desc = r.TryGetValue("description", out var de) ? de.GetString() ?? "" : "";
                tableOk = desc.Contains("ค่าบริการ")
                          && NumEq(CellStr(r, "qty"), "1")
                          && NumEq(CellStr(r, "unit_price"), "450.00")
                          && NumEq(CellStr(r, "amount"), "450.00");
            }
            sb.AppendLine($"row0 check (desc~ค่าบริการ, qty~1, unit~450.00, amount~450.00): {(tableOk ? "PASS" : "FAIL")}  rows={rows.Count}");
        }
        catch (Exception ex) { sb.AppendLine($"table parse error: {ex.Message}"); }

        sb.AppendLine(new string('-', 96));
        sb.AppendLine($"SCALARS: {pass}/{Fields.Length} passed   TABLE: {(tableOk ? "PASS" : "FAIL")}   NeedsReview={outcome.NeedsReview}");

        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT")
            ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "doc_rt_validation.txt"), sb.ToString());

        Assert.True(pass == Fields.Length && tableOk,
            $"\n{sb}\nFAILURES: {string.Join(", ", failures)}{(tableOk ? "" : ", line_item")}");
    }

    // ---- helpers ---------------------------------------------------------------

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true };

    private static string CellStr(Dictionary<string, JsonElement> r, string k)
        => r.TryGetValue(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

    private static string StripWs(string s) => Regex.Replace(s, @"\s+", "");

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    private static bool NumEq(string raw, string expected)
    {
        var m = Regex.Match(raw.Replace(",", ""), @"-?\d+(\.\d+)?");
        return m.Success
            && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var b)
            && a == b;
    }

    private static async Task<bool> SidecarUp()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var r = await http.GetAsync("http://localhost:8080/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
