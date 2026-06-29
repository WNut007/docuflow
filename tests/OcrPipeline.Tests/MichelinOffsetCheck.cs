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
                Col("quantity",    "STRING", 0.03m, 0.10m, anchor: true, sort: 0),
                Col("origin",      "STRING", 0.17m, 0.45m, sort: 1, mode: "ANCHOR", offset: -1),
                Col("articlecode", "STRING", 0.60m, 0.72m, sort: 2),     // for row identification in the dump
            }
        };

        var outcome = await svc.ProcessMultiPageAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        var got = rows.Select(r => (qty: Cell(r, "quantity"), art: Cell(r, "articlecode"),
                                    origin: Cell(r, "origin").Replace("\n", " ").Trim())).ToList();

        // EXACT per-row expected origin, in multi-page concatenated order (p1, p2, p3, p4-totals).
        // "US" = the leader's own group origin (United States); "" = a follower (or no metadata above) -> empty.
        // A future regression where a FOLLOWER starts leaking "United States" fails this (not the loose check).
        string[] expect =
        {
            "US","US","US",                 // p1: 6,14,9          (all leaders)
            "US","US","US","","","US",      // p2: 57,99,2,31,6,7  (2 is a LEADER; 31,6 followers)
            "US","","US","","US",           // p3: 11,14,1,1,12    (14, second-1 followers)
            "",                             // p4: 270 totals row  (no metadata above -> empty)
        };

        var sb = new StringBuilder();
        sb.AppendLine("Multi-page (ProcessMultiPageAsync) per-row origin — exact assertion");
        sb.AppendLine(new string('-', 70));
        var fails = new List<string>();
        for (int i = 0; i < got.Count || i < expect.Length; i++)
        {
            string origin = i < got.Count ? got[i].origin : "<missing row>";
            string e = i < expect.Length ? expect[i] : "<extra row>";
            bool ok = e == "US" ? origin.Contains("United States", StringComparison.OrdinalIgnoreCase)
                                : (e == "" && i < got.Count ? origin.Length == 0 : false);
            if (!ok) fails.Add($"row{i}: origin='{origin}' expected {(e == "US" ? "United States" : e == "" ? "(empty)" : e)}");
            sb.AppendLine($"  row{i,-2} qty={(i<got.Count?got[i].qty:"-"),-4} art={(i<got.Count?got[i].art:"-"),-14} origin=[{Trunc(origin,24),-24}] exp={e,-3} {(ok ? "ok" : "FAIL")}");
        }
        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_multipage.txt"), sb.ToString());

        Assert.True(got.Count == expect.Length && fails.Count == 0,
            $"\n{sb}\nrows={got.Count} expected={expect.Length}\n" + string.Join("\n", fails));
    }

    /// <summary>
    /// DIAGNOSTIC (read-only, no GroupInherit): reads the metadata block reading DOWNWARD from each anchor
    /// at +3 (brand), +4 (tyre), +5 (origin) to see what the +N direction actually returns under rule 5
    /// (own-half bound). Dumps per row; no assertion — it's a measurement to decide the direction.
    /// </summary>
    [Fact]
    public async Task Michelin_downward_offset_diagnostic()
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
            MinConfidence = 0.30m, ZonePage = 1, ZonePageRole = "FIRST",
            ZoneX = 0.03m, ZoneY = 0.45m, ZoneW = 0.95m, ZoneH = 0.51m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] = new()
            {
                Col("quantity", "STRING", 0.03m, 0.10m, anchor: true, sort: 0),
                Col("brand_p3", "STRING", 0.17m, 0.40m, sort: 1, mode: "ANCHOR", offset: 3),
                Col("tyre_p4",  "STRING", 0.17m, 0.40m, sort: 2, mode: "ANCHOR", offset: 4),
                Col("orig_p5",  "STRING", 0.17m, 0.45m, sort: 3, mode: "ANCHOR", offset: 5),
            }
        };
        var outcome = await svc.ProcessMultiPageAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        var sb = new StringBuilder();
        sb.AppendLine("DOWNWARD diagnostic — reading +3/+4/+5 below each anchor (rule 5 in effect, NO inherit)");
        sb.AppendLine(new string('-', 86));
        foreach (var r in rows)
            sb.AppendLine($"  qty={Cell(r,"quantity"),-5} brand(+3)=[{Trunc(Cell(r,"brand_p3").Replace("\n"," ").Trim(),22),-22}] tyre(+4)=[{Trunc(Cell(r,"tyre_p4").Replace("\n"," ").Trim(),22),-22}] origin(+5)=[{Cell(r,"orig_p5").Replace("\n"," ").Trim()}]");
        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_downward.txt"), sb.ToString());
    }

    /// <summary>
    /// Mechanism 2a: origin = LineOffset -1 + GroupInherit. Followers now INHERIT the group leader's origin
    /// (were empty under mechanism 1). Through the REAL multi-page path (ProcessMultiPageAsync), exact per
    /// row: EVERY line item (pages 1-3) -> United States; the page-4 totals row -> empty. A regression where
    /// a follower stops inheriting OR inherits from the wrong group fails this exact assertion. The sibling
    /// Michelin_multipage_path_applies_LineOffset (NO GroupInherit -> followers stay empty) proves the
    /// post-pass is a TRUE no-op when not opted in.
    /// </summary>
    [Fact]
    public async Task Michelin_multipage_group_inherit_fills_followers()
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
            MinConfidence = 0.30m, ZonePage = 1, ZonePageRole = "FIRST",
            ZoneX = 0.03m, ZoneY = 0.45m, ZoneW = 0.95m, ZoneH = 0.51m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] = new()
            {
                Col("quantity",    "STRING", 0.03m, 0.10m, anchor: true, sort: 0),
                Col("origin",      "STRING", 0.17m, 0.45m, sort: 1, mode: "ANCHOR", offset: -1, groupInherit: true),
                Col("articlecode", "STRING", 0.60m, 0.72m, sort: 2),
            }
        };

        var outcome = await svc.ProcessMultiPageAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        var got = rows.Select(r => (qty: Cell(r, "quantity"), art: Cell(r, "articlecode"),
                                    origin: Cell(r, "origin").Replace("\n", " ").Trim())).ToList();

        // With GroupInherit: EVERY line item (p1-3) -> United States (followers inherit their leader's),
        // only the page-4 totals row (270; no metadata above, no followers) stays empty.
        string[] expect =
        {
            "US","US","US",                 // p1: 6,14,9
            "US","US","US","US","US","US",  // p2: 57,99,2,31,6,7   (31,6 INHERIT from leader qty 2)
            "US","US","US","US","US",       // p3: 11,14,1,1,12     (14 + second-1 INHERIT)
            "",                             // p4: 270 totals row
        };

        var sb = new StringBuilder();
        sb.AppendLine("Mechanism 2a — multi-page per-row origin WITH GroupInherit (followers inherit)");
        sb.AppendLine(new string('-', 70));
        var fails = new List<string>();
        for (int i = 0; i < got.Count || i < expect.Length; i++)
        {
            string origin = i < got.Count ? got[i].origin : "<missing row>";
            string e = i < expect.Length ? expect[i] : "<extra row>";
            bool ok = e == "US" ? origin.Contains("United States", StringComparison.OrdinalIgnoreCase)
                                : (e == "" && i < got.Count ? origin.Length == 0 : false);
            if (!ok) fails.Add($"row{i}: origin='{origin}' expected {(e == "US" ? "United States" : "(empty)")}");
            sb.AppendLine($"  row{i,-2} qty={(i<got.Count?got[i].qty:"-"),-4} art={(i<got.Count?got[i].art:"-"),-14} origin=[{Trunc(origin,24),-24}] exp={e,-3} {(ok ? "ok" : "FAIL")}");
        }
        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_groupinherit.txt"), sb.ToString());

        Assert.True(got.Count == expect.Length && fails.Count == 0,
            $"\n{sb}\nrows={got.Count} expected={expect.Length}\n" + string.Join("\n", fails));
    }

    /// <summary>
    /// 2a — the VARYING-value proof that origin (all "United States") could NOT give. tyre_type at
    /// ANCHOR -2 + GroupInherit differs by group: B.F. Goodrich groups -> "Light Truck Tyre", Michelin
    /// groups -> "Passenger Car Radial Tyre". Followers 31/6 (group {2,31,6}, leader qty 2) must inherit
    /// "Passenger Car Radial Tyre" and NOT qty 99's "Light Truck Tyre". So a follower bleeding the
    /// PREVIOUS group's value (a wrong-group inheritance bug) fails this exact per-row assertion -- the
    /// thing the all-US origin test structurally cannot catch.
    /// </summary>
    [Fact]
    public async Task Michelin_multipage_group_inherit_tyre_type_reads_correct_group()
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
            MinConfidence = 0.30m, ZonePage = 1, ZonePageRole = "FIRST",
            ZoneX = 0.03m, ZoneY = 0.45m, ZoneW = 0.95m, ZoneH = 0.51m
        });
        var columns = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] = new()
            {
                Col("quantity",   "STRING", 0.03m, 0.10m, anchor: true, sort: 0),
                Col("tyre_type",  "STRING", 0.17m, 0.40m, sort: 1, mode: "ANCHOR", offset: -2, groupInherit: true),
                Col("articlecode","STRING", 0.60m, 0.72m, sort: 2),
            }
        };

        var outcome = await svc.ProcessMultiPageAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), columns, default);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            outcome.Values.Single(v => v.TargetProperty == "line_item").NormalizedValue ?? "[]")!;
        var got = rows.Select(r => (qty: Cell(r, "quantity"), art: Cell(r, "articlecode"),
                                    tyre: Cell(r, "tyre_type").Replace("\n", " ").Trim())).ToList();

        // LT = "Light Truck Tyre" (B.F. Goodrich groups), PCR = "Passenger Car Radial Tyre" (Michelin),
        // "" = the page-4 totals row. The KEY rows: p2 57/99 = LT, but 2 + its followers 31/6 = PCR.
        string[] expect =
        {
            "PCR","PCR","PCR",                 // p1: 6,14,9
            "LT","LT","PCR","PCR","PCR","PCR", // p2: 57,99,2,[31,6 INHERIT qty2's PCR],7
            "PCR","PCR","PCR","PCR","PCR",     // p3: 11,14,1,1,12
            "",                                // p4: 270 totals
        };

        var sb = new StringBuilder();
        sb.AppendLine("2a — multi-page tyre_type (ANCHOR -2 + GroupInherit): VARIES by group, proves correct-group inheritance");
        sb.AppendLine(new string('-', 78));
        var fails = new List<string>();
        for (int i = 0; i < got.Count || i < expect.Length; i++)
        {
            string tyre = i < got.Count ? got[i].tyre : "<missing row>";
            string e = i < expect.Length ? expect[i] : "<extra row>";
            bool ok = e switch
            {
                "LT"  => tyre.Contains("Light Truck", StringComparison.OrdinalIgnoreCase),
                "PCR" => tyre.Contains("Passenger Car Radial", StringComparison.OrdinalIgnoreCase),
                ""    => i < got.Count && tyre.Length == 0,
                _     => false,
            };
            if (!ok) fails.Add($"row{i}: tyre='{tyre}' expected {e}");
            sb.AppendLine($"  row{i,-2} qty={(i<got.Count?got[i].qty:"-"),-4} art={(i<got.Count?got[i].art:"-"),-14} tyre=[{Trunc(tyre,28),-28}] exp={e,-4} {(ok ? "ok" : "FAIL")}");
        }
        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT") ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "michelin_tyretype.txt"), sb.ToString());

        Assert.True(got.Count == expect.Length && fails.Count == 0,
            $"\n{sb}\nrows={got.Count} expected={expect.Length}\n" + string.Join("\n", fails));
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

    private static MappingTableColumn Col(string sub, string dt, decimal xs, decimal xe, bool anchor = false, int sort = 0, string? mode = null, int? offset = null, bool groupInherit = false)
        => new() { TargetSubProperty = sub, DataType = dt, ColXStart = xs, ColXEnd = xe, IsAnchor = anchor, SortOrder = sort, IsActive = true, LineSelectMode = mode, LineOffset = offset, GroupInherit = groupInherit };

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
