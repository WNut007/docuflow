using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Imaging;

namespace OcrPipeline.Web.Controllers;

/// <summary>
/// DEVELOPMENT-ONLY helpers. Every action hard-returns 404 outside the Development environment,
/// so this never does anything in Staging/Production. Lets you exercise the visual mapper with
/// realistic data (clickable text + table-cell boxes) without standing up Google Document AI.
/// </summary>
[Authorize]
[Route("dev")]
public sealed class DevController(
    IWebHostEnvironment env,
    IConfiguration config,
    IDocumentRepository documents,
    OcrRepository ocrRepo,
    MappingRepository mapping,
    PagePreviewRenderer previewRenderer) : Controller
{
    /// <summary>
    /// Seeds one Invoice document from samples/east-repair-invoice.png with a page preview and a
    /// synthetic OCR run (KEY/VALUE + LINE blocks and a line-items table — all with 0..1 bboxes),
    /// then redirects to the visual mapper for that document.
    /// </summary>
    [HttpGet("seed-sample")]
    public IActionResult SeedSample()
    {
        if (!env.IsDevelopment()) return NotFound();

        var samplePath = FindSample();
        if (samplePath is null)
            return Problem("samples/east-repair-invoice.png not found near the content root.");

        // copy into the upload root exactly like a real upload
        var uploadRoot = config["Storage:UploadRoot"] ?? "App_Data/uploads";
        Directory.CreateDirectory(uploadRoot);
        var storedPath = Path.Combine(uploadRoot, $"{Guid.NewGuid():N}.png");
        System.IO.File.Copy(samplePath, storedPath, overwrite: true);

        string sha;
        using (var read = System.IO.File.OpenRead(storedPath))
            sha = ExtractionService.ComputeSha256(read);

        var doc = new Document
        {
            OriginalFileName = "east-repair-invoice.png",
            StoredPath = storedPath,
            ContentType = "image/png",
            FileSizeBytes = new FileInfo(storedPath).Length,
            Sha256 = sha,
            SourceChannel = "DEV",
            StatusCode = "CAPTURED"
        };
        long docId = documents.Insert(doc);
        documents.SetClassification(docId, 1, 0.95m); // type 1 = Invoice (from 02_seed.sql)

        // render the page preview + record page dimensions (reuses Prompt 2 pipeline)
        var dpi = config.GetValue<int?>("Storage:PreviewDpi") ?? 200;
        var pages = previewRenderer.Render(storedPath, "image/png", dpi);
        documents.InsertPages(docId, pages.Select(p => new DocumentPage
        {
            DocumentId = docId, PageNumber = p.PageNumber, WidthPx = p.Width, HeightPx = p.Height
        }));

        // synthetic OCR (blocks + table cells, all with bboxes) -> parameterized inserts via the repo
        ocrRepo.SaveExtraction(docId, BuildExtraction());

        var tpl = mapping.GetActiveTemplateForType(1);
        return tpl is null
            ? RedirectToAction("Index", "Mapping")
            : RedirectToAction("Visual", "Mapping", new { templateId = tpl.TemplateId, documentId = docId });
    }

    // ---- helpers --------------------------------------------------------------

    private string? FindSample()
    {
        var dir = env.ContentRootPath;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "samples", "east-repair-invoice.png");
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    // East Repair ground truth (CLAUDE.md) as a synthetic extraction with plausible 0..1 boxes.
    private static OcrExtraction BuildExtraction()
    {
        var ex = new OcrExtraction { Engine = "DEV_SEED", EngineVersion = "seed-1", PageCount = 1 };

        void Kv(string key, string val, string? norm, decimal conf, decimal l, decimal t, decimal w, decimal h)
            => ex.TextBlocks.Add(new OcrTextBlock
            {
                PageNumber = 1, BlockType = "VALUE", Content = $"{key}: {val}", NormalizedContent = norm,
                Confidence = conf, BBoxLeft = l, BBoxTop = t, BBoxWidth = w, BBoxHeight = h
            });
        void Line(string text, decimal conf, decimal l, decimal t, decimal w, decimal h)
            => ex.TextBlocks.Add(new OcrTextBlock
            {
                PageNumber = 1, BlockType = "LINE", Content = text,
                Confidence = conf, BBoxLeft = l, BBoxTop = t, BBoxWidth = w, BBoxHeight = h
            });

        Line("East Repair Inc.", 0.98m, 0.06m, 0.05m, 0.40m, 0.030m);
        Line("Bill To: John Smith", 0.95m, 0.06m, 0.17m, 0.35m, 0.028m);

        Kv("Invoice No", "US-001", "US-001", 0.98m, 0.60m, 0.120m, 0.30m, 0.025m);
        Kv("Date", "11/02/2019", "2019-02-11", 0.96m, 0.60m, 0.155m, 0.30m, 0.025m);
        Kv("PO Number", "2312/2019", "2312/2019", 0.95m, 0.60m, 0.190m, 0.30m, 0.025m);
        Kv("Due Date", "26/02/2019", "2019-02-26", 0.96m, 0.60m, 0.225m, 0.30m, 0.025m);
        Kv("Subtotal", "145.00", "145.00", 0.97m, 0.62m, 0.740m, 0.26m, 0.025m);
        Kv("Sales Tax 6.25%", "9.06", "9.06", 0.94m, 0.62m, 0.775m, 0.26m, 0.025m);
        Kv("TOTAL", "154.06", "154.06", 0.98m, 0.62m, 0.815m, 0.26m, 0.030m);

        var table = new OcrTable { PageNumber = 1, TableIndex = 0, RowCount = 4, ColumnCount = 4, Confidence = 0.95m };
        var cols = new (decimal L, decimal W)[] { (0.06m, 0.44m), (0.52m, 0.10m), (0.64m, 0.16m), (0.82m, 0.14m) };
        string[] headers = { "Description", "Qty", "Unit Price", "Amount" };
        var rows = new (string Desc, string Qty, string Unit, string Amt)[]
        {
            ("Front and rear brake cables", "1", "100.00", "100.00"),
            ("New set of pedal arms",       "2", "15.00",  "30.00"),
            ("Labor 3hrs",                  "3", "5.00",   "15.00"),
        };
        const decimal rowH = 0.035m, top0 = 0.45m;

        void Cell(int r, int c, bool header, string content, string? norm)
            => table.Cells.Add(new OcrTableCell
            {
                RowIndex = r, ColIndex = c, IsHeader = header, Content = content, NormalizedContent = norm,
                Confidence = 0.95m,
                BBoxLeft = cols[c].L, BBoxTop = top0 + r * rowH, BBoxWidth = cols[c].W, BBoxHeight = rowH - 0.004m
            });

        for (int c = 0; c < 4; c++) Cell(0, c, true, headers[c], null);
        for (int r = 0; r < rows.Length; r++)
        {
            Cell(r + 1, 0, false, rows[r].Desc, null);
            Cell(r + 1, 1, false, rows[r].Qty, rows[r].Qty);
            Cell(r + 1, 2, false, rows[r].Unit, rows[r].Unit);
            Cell(r + 1, 3, false, rows[r].Amt, rows[r].Amt);
        }
        ex.Tables.Add(table);
        return ex;
    }
}
