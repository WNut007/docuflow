using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Mapping;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class DocumentsController(
    IDocumentRepository documents,
    OcrRepository ocrRepo,
    IMappingRepository mappingRepo,
    PipelineService pipeline,
    PagePreviewRenderer previewRenderer,
    IConfiguration config) : Controller
{
    public IActionResult Index()
        => View(documents.GetRecent());

    [HttpGet]
    public IActionResult Upload() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
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
            UploadedByUserId = userId
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

        // run the pipeline inline (mockup); in prod this is enqueued
        await pipeline.ProcessAsync(docId, userId, ct);

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
            MappedValues = result?.values ?? new List<MappedValueRow>()
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

        var values = result.Value.values.Select(v => new ReviewValueModel
        {
            ResultValueId = v.ResultValueId,
            TargetProperty = v.TargetProperty,
            RawValue = v.RawValue,
            NormalizedValue = v.NormalizedValue,
            Confidence = v.Confidence,
            IsBelowThreshold = v.IsBelowThreshold,
            BandClass = ConfidenceBands.CssClass(ConfidenceBands.Band(v.Confidence, cutoff)),
            BlockId = BlockIdFromSourceRef(v.SourceRef)
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
    public IActionResult ReviewSave([FromBody] ReviewSavePayload payload)
    {
        if (payload is null || payload.DocumentId <= 0) return BadRequest();
        var doc = documents.GetById(payload.DocumentId);
        if (doc is null) return NotFound();

        int applied = 0;
        foreach (var c in payload.Corrections)
            applied += mappingRepo.UpdateResultValue(payload.DocumentId, c.ResultValueId, c.NormalizedValue);

        var status = ReviewWorkflow.Finalize(documents, doc, applied, GetUserId());
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
}
