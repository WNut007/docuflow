using System.Globalization;
using System.Net.Http;
using System.Text;
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
/// GATE #6 (English no-regress): the route-(a) switch to th_PP-OCRv5_mobile_rec must NOT regress dense
/// Latin (Michelin article codes / tire specs / amounts). Reads known Michelin page-1 Latin regions
/// through the SAME real path the Thai test uses (ZonalExtractionService + v5 PaddleRegionOcrEngine,
/// native-resolution crops). Baseline reference: the prior shipping default is Tesseract, which the
/// codebase notes is weak on dense Latin tables; the bar here is ABSOLUTE correctness of every code/spec
/// — a single garble would flag a regression and the need to escalate to route (b) per-lang models.
/// </summary>
public sealed class MichelinLatinNoRegress
{
    private sealed record Spec(int Id, string Prop, string Expected, Match Mode,
        decimal X, decimal Y, decimal W, decimal H);
    private enum Match { Exact, Numeric, Contains }

    // page-1 dense-Latin regions (page-normalized), from the full-page v5 measurement of michelin-invoice.pdf
    private static readonly Spec[] Fields =
    {
        new(1, "invoice_no",   "15398231",                                       Match.Exact,    0.868m, 0.031m, 0.075m, 0.018m),
        new(2, "container_no", "CMAU472280",                                     Match.Exact,    0.376m, 0.501m, 0.100m, 0.018m),
        new(3, "article_1",    "952972.4705",                                    Match.Exact,    0.629m, 0.635m, 0.095m, 0.018m),
        new(4, "tire_spec_1",  "245/45 ZR18 (100Y) XL TL PILOT SPORT 4 S MI",    Match.Contains, 0.178m, 0.635m, 0.290m, 0.018m),
        new(5, "unit_price_1", "76.81",                                          Match.Numeric,  0.786m, 0.634m, 0.052m, 0.018m),
        new(6, "total_1",      "460.86",                                         Match.Numeric,  0.913m, 0.634m, 0.060m, 0.018m),
        new(7, "article_2",    "644530.4705",                                    Match.Exact,    0.629m, 0.751m, 0.095m, 0.018m),
        new(8, "tire_spec_2",  "285/35 ZR20 (104Y) EXTRA LOAD TL PILOT SPORT 4 S MI", Match.Contains, 0.178m, 0.751m, 0.355m, 0.018m),
        new(9, "total_2",      "1,506.54",                                       Match.Numeric,  0.902m, 0.749m, 0.072m, 0.018m),
    };

    [Fact]
    public async Task Michelin_dense_latin_reads_clean_through_v5_path()
    {
        string? sample = FindUp(Path.Combine("samples", "michelin-invoice.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return;

        // PRODUCTION default MinOcrWidth=2200: crop upscaling is engine-owned, so the Paddle path gets
        // native crops by construction (this value only affects a Tesseract fallback). Real prod wiring.
        var opts = Options.Create(new TesseractOptions { TessdataPath = "", Languages = "eng", Dpi = 300, MinOcrWidth = 2200 });
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

        var template = new MappingTemplate { TemplateId = 2, TargetModel = "MichelinInvoice", MappingMode = "ZONAL" };
        foreach (var f in Fields)
            template.Fields.Add(new MappingField
            {
                FieldId = f.Id, TargetProperty = f.Prop, DataType = "STRING", SourceType = "KEY_VALUE",
                MinConfidence = 0.40m, ZonePage = 1, ZoneX = f.X, ZoneY = f.Y, ZoneW = f.W, ZoneH = f.H, ZoneOcrHint = "TEXT"
            });

        var doc = new Document { DocumentId = 2, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "eng" };
        var outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), null, default);

        var sb = new StringBuilder();
        sb.AppendLine("GATE #6 — English no-regress: dense Michelin Latin via real ZonalExtractionService + v5 PaddleRegionOcrEngine");
        sb.AppendLine("doc=samples/michelin-invoice.pdf page=1   engine=th_PP-OCRv5_mobile_rec (route a)   crops=native (engine-owned upscale; MinOcrWidth=2200 affects Tesseract only)");
        sb.AppendLine(new string('-', 100));
        sb.AppendLine($"{"field",-14}{"expected",-46}{"raw read",-30}{"conf",-7}result");
        sb.AppendLine(new string('-', 100));

        int pass = 0; var failures = new List<string>();
        foreach (var f in Fields)
        {
            var mv = outcome.Values.Single(v => v.TargetProperty == f.Prop);
            string raw = (mv.RawValue ?? "").Replace("\n", " ").Trim();
            bool ok = f.Mode switch
            {
                Match.Exact    => raw.Trim() == f.Expected,
                Match.Numeric  => NumEq(raw, f.Expected),
                Match.Contains => raw.Contains(f.Expected),
                _ => false
            };
            if (ok) pass++; else failures.Add(f.Prop);
            sb.AppendLine($"{f.Prop,-14}{Trunc(f.Expected, 44),-46}{Trunc(raw, 28),-30}{mv.Confidence,-7:F2}{(ok ? "PASS" : "FAIL")}");
        }
        sb.AppendLine(new string('-', 100));
        sb.AppendLine($"DENSE LATIN: {pass}/{Fields.Length} read exact  =>  {(pass == Fields.Length ? "NO REGRESSION" : "REGRESSION — escalate to route (b)")}");

        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_noregress.txt"), sb.ToString());

        Assert.True(pass == Fields.Length, $"\n{sb}\nFAILURES: {string.Join(", ", failures)}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    private static bool NumEq(string raw, string expected)
    {
        var m = Regex.Match(raw.Replace(",", ""), @"-?\d+(\.\d+)?");
        return m.Success
            && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(expected.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var b)
            && a == b;
    }

    private static async Task<bool> SidecarUp()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; return (await h.GetAsync("http://localhost:8080/health")).IsSuccessStatusCode; }
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
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
