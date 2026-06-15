using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Queue;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class DocumentsController(
    IDocumentRepository documents,
    OcrRepository ocrRepo,
    IMappingRepository mappingRepo,
    IPipelineRunner pipeline,
    IJobQueue queue,
    OcrPipeline.Web.Services.Export.IExportQueue exportQueue,
    IExportRepository exportRepo,
    ProcessorRepository processors,
    IOcrEngine ocrEngine,
    PagePreviewRenderer previewRenderer,
    MappingEngine mappingEngine,
    TextNormalizer normalizer,
    IConfiguration config) : Controller
{
    public IActionResult Index()
        => View(documents.GetRecent());

    [HttpGet]
    public IActionResult Upload() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Upload(IFormFile file, string? ocrLanguages, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please choose a file.");
            return View();
        }

        var uploadRoot = config["Storage:UploadRoot"] ?? "App_Data/uploads";
        Directory.CreateDirectory(uploadRoot);

        var storedName = $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
        var storedPath = Path.Combine(uploadRoot, storedName);

        await using (var fs = System.IO.File.Create(storedPath))
            await file.CopyToAsync(fs, ct);

        string sha;
        await using (var read = System.IO.File.OpenRead(storedPath))
            sha = ExtractionService.ComputeSha256(read);

        var userId = GetUserId();
        var doc = new Document
        {
            OriginalFileName = file.FileName,
            StoredPath = storedPath,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Sha256 = sha,
            SourceChannel = "UPLOAD",
            StatusCode = "CAPTURED",
            UploadedByUserId = userId,
            OcrLanguages = NormalizeOcrLanguages(ocrLanguages)
        };
        var docId = documents.Insert(doc);
        documents.LogEvent(docId, "CAPTURE", null, "CAPTURED", "File uploaded", userId);

        // rasterize page previews (PDF -> per-page PNG; image -> single PNG) for the mapping UI
        try
        {
            var dpi = config.GetValue<int?>("Storage:PreviewDpi") ?? 200;
            var pages = previewRenderer.Render(storedPath, file.ContentType, dpi);
            documents.InsertPages(docId, pages.Select(p => new DocumentPage
            {
                DocumentId = docId, PageNumber = p.PageNumber, WidthPx = p.Width, HeightPx = p.Height
            }));
        }
        catch (Exception ex)
        {
            // previews are non-fatal: the pipeline can still extract; UI image just 404s
            documents.LogEvent(docId, "PREVIEW", "CAPTURED", "CAPTURED",
                $"Preview rendering failed: {ex.Message}", userId);
        }

        // Honour the active processor's mode: REALTIME runs inline; otherwise (QUEUE or none) the
        // document is enqueued and processed off the request thread by the BackgroundService worker.
        var processor = processors.GetActiveForEngine(ocrEngine.Name);
        if (string.Equals(processor?.ProcessorMode, "REALTIME", StringComparison.OrdinalIgnoreCase))
        {
            await pipeline.ProcessAsync(docId, userId, ct);
        }
        else
        {
            await queue.EnqueueAsync(docId, ct);
            documents.LogEvent(docId, "QUEUE", "CAPTURED", "CAPTURED", "Queued for processing", userId);
        }

        return RedirectToAction(nameof(Detail), new { id = docId });
    }

    public IActionResult Detail(long id)
    {
        var doc = documents.GetById(id);
        if (doc is null) return NotFound();

        var result = mappingRepo.GetLatestResult(id);
        var vm = new DocumentDetailViewModel
        {
            Document = doc,
            Ocr = ocrRepo.LoadLatest(id),
            Properties = ocrRepo.LoadProperties(id),
            MappingConfidence = result?.overall,
            MappingNeedsReview = result?.needsReview ?? false,
            MappedJson = result?.json,
            MappedValues = result?.values ?? new List<MappedValueRow>(),
            ExportLogs = exportRepo.GetLogsForDocument(id)
        };
        return View(vm);
    }

    // ---- Accuracy review (Prompt 5) ---------------------------------------

    [HttpGet]
    public IActionResult Review(long id)
    {
        var doc = documents.GetById(id);
        if (doc is null) return NotFound();

        decimal cutoff = config.GetValue<decimal?>("Ocr:MinPageConfidence") ?? 0.60m;
        var result = mappingRepo.GetLatestResult(id);

        if (result is null)
            return View(new ReviewViewModel
            {
                Document = doc, HasResult = false, Cutoff = cutoff, PageCount = doc.PageCount
            });

        // Hydrate each value with its field's zone rect (focus->highlight) and, for the line_item
        // TABLE_CELL field, its columns + parsed display rows (editable table instead of raw JSON).
        var template = mappingRepo.GetTemplateById(result.Value.templateId);
        var fieldsById = template?.Fields.ToDictionary(f => f.FieldId) ?? new Dictionary<int, MappingField>();
        var columnsByField = mappingRepo.GetTableColumns(result.Value.templateId);

        var values = result.Value.values.Select(v =>
        {
            fieldsById.TryGetValue(v.FieldId, out var field);
            ReviewTableModel? table = null;
            if (field is not null
                && string.Equals(field.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase)
                && columnsByField.TryGetValue(v.FieldId, out var rawCols))
            {
                var cols = rawCols.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList();
                if (cols.Count > 0)
                    table = new ReviewTableModel
                    {
                        Columns = cols.Select(c => new ReviewColumn
                        {
                            SubProperty = c.TargetSubProperty, DataType = c.DataType, IsAnchor = c.IsAnchor
                        }).ToList(),
                        Rows = LineItemTable.Parse(v.NormalizedValue, cols)
                    };
            }

            return new ReviewValueModel
            {
                ResultValueId = v.ResultValueId,
                FieldId = v.FieldId,
                TargetProperty = v.TargetProperty,
                RawValue = v.RawValue,
                NormalizedValue = v.NormalizedValue,
                Confidence = v.Confidence,
                IsBelowThreshold = v.IsBelowThreshold,
                BandClass = ConfidenceBands.CssClass(ConfidenceBands.Band(v.Confidence, cutoff)),
                BlockId = BlockIdFromSourceRef(v.SourceRef),
                ZoneX = field?.ZoneX, ZoneY = field?.ZoneY, ZoneW = field?.ZoneW, ZoneH = field?.ZoneH,
                ZonePage = field?.ZonePage ?? 1,
                Table = table
            };
        }).ToList();

        return View(new ReviewViewModel
        {
            Document = doc,
            HasResult = true,
            NeedsReview = result.Value.needsReview,
            OverallConfidence = result.Value.overall,
            Cutoff = cutoff,
            PageCount = doc.PageCount,
            Values = values
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewSave([FromBody] ReviewSavePayload payload, CancellationToken ct)
    {
        if (payload is null || payload.DocumentId <= 0) return BadRequest();
        var doc = documents.GetById(payload.DocumentId);
        if (doc is null) return NotFound();

        int applied = 0;
        foreach (var c in payload.Corrections)
            applied += mappingRepo.UpdateResultValue(payload.DocumentId, c.ResultValueId, c.NormalizedValue);

        // Line_item tables: re-type each edited cell server-side through the SAME path as extraction
        // (MappingEngine.NormalizeTyped), so qty stays int / prices stay decimal regardless of client.
        if (payload.TableCorrections.Count > 0)
        {
            int templateId = mappingRepo.GetLatestResult(payload.DocumentId)?.templateId ?? 0;
            var columnsByField = mappingRepo.GetTableColumns(templateId);
            foreach (var t in payload.TableCorrections)
            {
                if (!columnsByField.TryGetValue(t.FieldId, out var rawCols)) continue;
                var cols = rawCols.Where(col => col.IsActive).OrderBy(col => col.SortOrder).ToList();
                if (cols.Count == 0) continue;

                var order = normalizer.InferDayMonthOrder(t.Rows.SelectMany(r => r.Values));
                var typed = LineItemTable.BuildTypedRows(
                    cols, t.Rows, (dt, raw) => mappingEngine.NormalizeTyped(dt, raw, order));
                var json = JsonSerializer.Serialize(typed);
                applied += mappingRepo.UpdateResultValue(payload.DocumentId, t.ResultValueId, json);
            }
        }

        var status = ReviewWorkflow.Finalize(documents, doc, applied, GetUserId());

        // Auto-export on VALIDATED (off the request thread); MAPPED-no-review docs export manually.
        if (status == "VALIDATED")
            await exportQueue.EnqueueAsync(payload.DocumentId, ct);

        return Json(new { ok = true, status, corrected = applied });
    }

    private static string? BlockIdFromSourceRef(string? sourceRef)
    {
        const string prefix = "TextBlock:";
        if (sourceRef is not null && sourceRef.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(sourceRef.AsSpan(prefix.Length), out var textBlockId))
            return $"block-{textBlockId}";
        return null;
    }

    private int? GetUserId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>
    /// Validates the per-document OCR language choice against an allow-list so only known language
    /// strings ever reach the OCR engine / tessdata lookup. "Auto"/blank/unknown returns null,
    /// meaning "use the configured default" (Ocr:Tesseract:Languages).
    /// </summary>
    private static string? NormalizeOcrLanguages(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "eng" => "eng",
        "tha" => "tha",
        "tha+eng" or "eng+tha" => "tha+eng",
        _ => null
    };
}
