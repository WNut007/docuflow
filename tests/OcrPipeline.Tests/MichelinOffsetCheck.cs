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
/// Validation for signed LineOffset (mechanism 1). Real zonal path over Michelin pages 1-3 with
/// origin = LineOffset -1 (line above the anchor) and our_reference = -5. Proves: leaders return their OWN
/// origin/reference (page-3 A -> 15821327, NOT the next item's 15821328 — the provenance the below-only
/// design would have gotten silently wrong); followers return EMPTY (rule 4); and the page-2 reference is
/// EMPTY (its above-block has no reference — the flagged limitation needing label-anchored). Never wrong.
/// </summary>
public sealed class MichelinOffsetCheck
{
    // expected per page (top-to-bottom). origin: "US" = contains United States, "" = empty.
    // ref: the leader's OWN group reference, "" = empty (follower, or page-2 ref-not-above).
    private static readonly Dictionary<int, (string[] origin, string[] reference, int[] qty)> Gt = new()
    {
        // origin(-1): leaders contain "United States", followers empty. our_reference(-5) with the rule-5
        // own-half bound: only a FIRST item (no previous anchor -> full above-block) can reach the ref at -5;
        // every other row is safely EMPTY (the ref is in the neighbour's half on split layouts, or the own
        // block is shorter than 5) -> NEVER wrong, but incomplete -> our_reference needs label-anchored.
        [1] = (new[]{"US","US","US"},                  new[]{"15821320","",""},        new[]{6,14,9}),
        [2] = (new[]{"US","US","US","","","US"},        new[]{"","","","","",""},        new[]{57,99,2,31,6,7}),
        [3] = (new[]{"US","","US","","US"},             new[]{"15821327","","","",""},   new[]{11,14,1,1,12}),
    };

    [Fact]
    public async Task Michelin_signed_offset_origin_and_reference()
    {
        string? sample = FindUp(Path.Combine("samples", "michelin-invoice.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return;

        var svc = BuildSvc();
        var doc = new Document { DocumentId = 65, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "eng" };

        var sb = new StringBuilder();
        sb.AppendLine("Signed LineOffset — Michelin  origin(-1)  +  our_reference(-5)  — real zonal path");
        sb.AppendLine(new string('-', 92));
        var fails = new List<string>();

        foreach (var page in new[] { 1, 2, 3 })
        {
            var rows = await Extract(svc, doc, page);
            var (gtOrigin, gtRef, gtQty) = Gt[page];
            sb.AppendLine($"PAGE {page}");
            for (int i = 0; i < rows.Count; i++)
            {
                string qty = rows[i].qty, origin = rows[i].origin, refr = rows[i].reference;
                string eo = i < gtOrigin.Length ? gtOrigin[i] : "?";
                string er = i < gtRef.Length ? gtRef[i] : "?";
                bool originOk = eo == "US" ? origin.Contains("United States", StringComparison.OrdinalIgnoreCase)
                                           : origin.Trim().Length == 0;
                bool refOk = er.Length == 0 ? refr.Trim().Length == 0 : refr.Contains(er);
                if (!originOk) fails.Add($"p{page} row{i} origin='{origin}' expected {eo}");
                if (!refOk) fails.Add($"p{page} row{i} ref='{refr}' expected '{er}'");
                sb.AppendLine($"  qty={qty,-3}  origin=[{Trunc(origin,28),-28}] {(originOk?"ok":"FAIL")}   our_reference=[{Trunc(refr,16),-16}] {(refOk?"ok":"FAIL")}");
            }
            sb.AppendLine();
        }
        sb.AppendLine(new string('-', 92));
        sb.AppendLine(fails.Count == 0
            ? "ALL OK — leaders return own origin+reference (provenance correct), followers empty, page-2 ref empty. Zero wrong values."
            : "FAILURES:\n  " + string.Join("\n  ", fails));

        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_offset.txt"), sb.ToString());

        Assert.True(fails.Count == 0, "\n" + sb);
    }

    /// <summary>
    /// Regression for the live bug: template 12 is role-tagged FIRST, so the app runs ProcessMultiPageAsync,
    /// which rebuilds each column via MultiPageColumns.Resolve — that path dropped LineOffset (copied the
    /// LineSelect* fields but not LineOffset) -> offset 0 -> the spec line sliced by the origin x-range =
    /// spec fragments. The single-page Extract() harness above never hit Resolve, so it stayed green. This
    /// exercises the SAME multi-page path the live app uses: origin must be "United States" or empty, never
    /// a spec fragment.
    /// </summary>
    [Fact]
    public async Task Michelin_multipage_path_applies_LineOffset()
    {
        string? sample = FindUp(Path.Combine("samples", "michelin-invoice.pdf"));
        if (sample is null) return;
        if (!await SidecarUp()) return;

        var svc = BuildSvc();
        var doc = new Document { DocumentId = 65, StoredPath = sample, ContentType = "application/pdf", OcrLanguages = "eng", PageCount = 4 };

        var template = new MappingTemplate { TemplateId = 99, TargetModel = "Michelin", MappingMode = "ZONAL" };
        template.Fields.Add(new MappingField
        {
            FieldId = 1, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
            MinConfidence = 0.30m, ZonePage = 1, ZonePageRole = "FIRST",     // FIRST role -> multi-page path
            ZoneX = 0.03m, ZoneY = 0.45m, ZoneW = 0.95m, ZoneH = 0.51m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] = new()
            {
                Col("quantity", "STRING", 0.03m, 0.10m, anchor: true, sort: 0),
                Col("origin",   "STRING", 0.17m, 0.45m, sort: 1, mode: "ANCHOR", offset: -1),
            }
        };

        var outcome = await svc.ProcessMultiPageAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        var origins = rows.Select(r => Cell(r, "origin").Replace("\n", " ").Trim()).ToList();

        // LineOffset applied -> every origin is "United States" (a leader) or empty; NEVER a spec fragment.
        bool anyUS = origins.Any(o => o.Contains("United States", StringComparison.OrdinalIgnoreCase));
        bool clean = origins.All(o => o.Length == 0 || o.Contains("United States", StringComparison.OrdinalIgnoreCase));
        Assert.True(anyUS && clean,
            "Multi-page origin must be 'United States' or empty (LineOffset applied through MultiPageColumns.Resolve). Got:\n  "
            + string.Join("\n  ", origins));
    }

    private async Task<List<(string qty, string origin, string reference)>> Extract(
        ZonalExtractionService svc, Document doc, int page)
    {
        var template = new MappingTemplate { TemplateId = 99, TargetModel = "Michelin", MappingMode = "ZONAL" };
        template.Fields.Add(new MappingField
        {
            FieldId = 1, TargetProperty = "line_item", DataType = "STRING", SourceType = "TABLE_CELL",
            MinConfidence = 0.30m, ZonePage = page, ZoneX = 0.03m, ZoneY = 0.45m, ZoneW = 0.95m, ZoneH = 0.51m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] = new()
            {
                Col("quantity",      "STRING", 0.03m, 0.10m, anchor: true, sort: 0),                          // ALL (default)
                Col("origin",        "STRING", 0.17m, 0.45m, sort: 1, mode: "ANCHOR", offset: -1),            // line ABOVE
                Col("our_reference", "STRING", 0.36m, 0.55m, sort: 2, mode: "ANCHOR", offset: -5),            // 5 lines ABOVE
            }
        };
        var outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        return rows.Select(r => (Cell(r, "quantity"), Cell(r, "origin").Replace("\n", " ").Trim(),
                                 Cell(r, "our_reference").Replace("\n", " ").Trim())).ToList();
    }

    private static ZonalExtractionService BuildSvc()
    {
        var opts = Options.Create(new TesseractOptions { TessdataPath = "", Languages = "eng", Dpi = 300, MinOcrWidth = 2200 });
        var pre = new ImagePreprocessor(); var norm = new TextNormalizer();
        var tess = new TesseractOcrEngine(opts, pre, norm);
        var paddle = new PaddleRegionOcrEngine(new SimpleHttpClientFactory(), tess,
            Options.Create(new PaddleOptions { BaseUrl = "http://localhost:8080", TimeoutSeconds = 120 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaddleRegionOcrEngine>.Instance);
        var eng = new MappingEngine(new TransformerPipeline(Array.Empty<IValueTransformer>()), norm);
        return new ZonalExtractionService(paddle, pre, new PagePreviewRenderer(), eng, norm, opts,
            Options.Create(new LineItemConsolidationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ZonalExtractionService>.Instance);
    }

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0, string? mode = null, int? offset = null)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true, LineSelectMode = mode, LineOffset = offset };

    private static string Cell(Dictionary<string, JsonElement> r, string k)
        => r.TryGetValue(k, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    private static async Task<bool> SidecarUp()
    { try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; return (await h.GetAsync("http://localhost:8080/health")).IsSuccessStatusCode; } catch { return false; } }
    private sealed class SimpleHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        { string c = Path.Combine(dir.FullName, relative); if (File.Exists(c)) return c; }
        return null;
    }
}
