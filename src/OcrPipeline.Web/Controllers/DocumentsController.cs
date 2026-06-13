using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Imaging;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class DocumentsController(
    IDocumentRepository documents,
    OcrRepository ocrRepo,
    MappingRepository mappingRepo,
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

    private int? GetUserId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
