using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Imaging;

namespace OcrPipeline.Web.Services;

/// <summary>
/// Stores an uploaded file and rasterizes its page previews (PDF -> per-page PNG; image -> one PNG),
/// inserting the Document + DocumentPage rows. Shared by the document upload flow and the
/// template-sample upload (the zone-designer backdrop), so both produce a drawable, page-counted
/// document the same way. Page rendering is best-effort: a failure is logged and swallowed (the row
/// still exists; the mapping UI image just 404s) so capture never fails on a preview hiccup.
/// </summary>
public interface IDocumentIngestionService
{
    Task<long> StoreAndRasterizeAsync(IFormFile file, string sourceChannel, string statusCode,
        int? templateId, int? userId, string? ocrLanguages, CancellationToken ct = default);
}

public sealed class DocumentIngestionService(
    IDocumentRepository documents,
    PagePreviewRenderer previewRenderer,
    IConfiguration config) : IDocumentIngestionService
{
    public async Task<long> StoreAndRasterizeAsync(IFormFile file, string sourceChannel, string statusCode,
        int? templateId, int? userId, string? ocrLanguages, CancellationToken ct = default)
    {
        var uploadRoot = config["Storage:UploadRoot"] ?? "App_Data/uploads";
        Directory.CreateDirectory(uploadRoot);

        var storedName = $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
        var storedPath = Path.Combine(uploadRoot, storedName);
        await using (var fs = System.IO.File.Create(storedPath))
            await file.CopyToAsync(fs, ct);

        string sha;
        await using (var read = System.IO.File.OpenRead(storedPath))
            sha = ExtractionService.ComputeSha256(read);

        var doc = new Document
        {
            OriginalFileName = file.FileName,
            StoredPath = storedPath,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Sha256 = sha,
            SourceChannel = sourceChannel,
            StatusCode = statusCode,
            UploadedByUserId = userId,
            OcrLanguages = ocrLanguages,
            TemplateId = templateId
        };
        var docId = documents.Insert(doc);

        // Page previews are non-fatal: on failure the pipeline can still extract and the UI image 404s.
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
            documents.LogEvent(docId, "PREVIEW", statusCode, statusCode,
                $"Preview rendering failed: {ex.Message}", userId);
        }
        return docId;
    }
}
