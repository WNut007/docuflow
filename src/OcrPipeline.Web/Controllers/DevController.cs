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
    IMappingRepository mapping,
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

        // Boxes measured against samples/east-repair-invoice.png (750x1061), normalized 0..1.

        // header / vendor (top-left) + Bill To
        Line("East Repair Inc.", 0.98m, 0.073m, 0.062m, 0.210m, 0.028m);
        Line("Bill To: John Smith", 0.95m, 0.073m, 0.252m, 0.110m, 0.022m);

        // meta block (upper-right); each box frames the VALUE
        Kv("Invoice No", "US-001",     "US-001",     0.98m, 0.840m, 0.227m, 0.090m, 0.022m);
        Kv("Date",       "11/02/2019", "2019-02-11", 0.96m, 0.795m, 0.253m, 0.135m, 0.022m);
        Kv("PO Number",  "2312/2019",  "2312/2019",  0.95m, 0.815m, 0.279m, 0.115m, 0.022m);
        Kv("Due Date",   "26/02/2019", "2019-02-26", 0.96m, 0.795m, 0.305m, 0.135m, 0.022m);

        // totals (right side, just below the table)
        Kv("Subtotal",        "145.00", "145.00", 0.97m, 0.795m, 0.492m, 0.090m, 0.022m);
        Kv("Sales Tax 6.25%", "9.06",   "9.06",   0.94m, 0.825m, 0.521m, 0.060m, 0.022m);
        Kv("TOTAL",           "154.06", "154.06", 0.98m, 0.775m, 0.551m, 0.110m, 0.026m);

        // line-items table: columns QTY | DESCRIPTION | UNIT PRICE | AMOUNT (matches the image)
        var table = new OcrTable { PageNumber = 1, TableIndex = 0, RowCount = 4, ColumnCount = 4, Confidence = 0.95m };
        var cols = new (decimal L, decimal W)[]
        {
            (0.073m, 0.082m),   // QTY
            (0.155m, 0.410m),   // DESCRIPTION
            (0.565m, 0.190m),   // UNIT PRICE
            (0.755m, 0.172m),   // AMOUNT
        };
        string[] headers = { "QTY", "DESCRIPTION", "UNIT PRICE", "AMOUNT" };
        var rows = new (string Qty, string Desc, string Unit, string Amt)[]
        {
            ("1", "Front and rear brake cables", "100.00", "100.00"),
            ("2", "New set of pedal arms",       "15.00",  "30.00"),
            ("3", "Labor 3hrs",                  "5.00",   "15.00"),
        };
        decimal[] rowTop = { 0.360m, 0.391m, 0.422m, 0.453m }; // header, row1, row2, row3
        const decimal rowH = 0.030m;

        void Cell(int r, int c, bool header, string content, string? norm)
            => table.Cells.Add(new OcrTableCell
            {
                RowIndex = r, ColIndex = c, IsHeader = header, Content = content, NormalizedContent = norm,
                Confidence = 0.95m,
                BBoxLeft = cols[c].L, BBoxTop = rowTop[r], BBoxWidth = cols[c].W, BBoxHeight = rowH
            });

        for (int c = 0; c < 4; c++) Cell(0, c, true, headers[c], null);
        for (int r = 0; r < rows.Length; r++)
        {
            Cell(r + 1, 0, false, rows[r].Qty,  rows[r].Qty);
            Cell(r + 1, 1, false, rows[r].Desc, null);
            Cell(r + 1, 2, false, rows[r].Unit, rows[r].Unit);
            Cell(r + 1, 3, false, rows[r].Amt,  rows[r].Amt);
        }
        ex.Tables.Add(table);
        return ex;
    }
}
