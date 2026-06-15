using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Template-based (zonal) extraction: for a ZONAL template, OCR ONLY inside each field's drawn zone
/// and feed the result straight into the field — no reliance on whatever blocks full-page OCR would
/// produce. The page is deskewed once, then each zone is cropped, upscaled, and read with a tight
/// PageSegMode + optional whitelist (<see cref="ZoneHint"/>). Normalization / transformers / review
/// flags all run through <see cref="MappingEngine.RunZonalAsync"/> — the same path as OCR-first.
/// </summary>
public sealed class ZonalExtractionService(
    IRegionOcrEngine regionOcr,
    ImagePreprocessor preprocessor,
    PagePreviewRenderer previewRenderer,
    MappingEngine mappingEngine,
    IOptions<TesseractOptions> tessOptions)
{
    private readonly TesseractOptions _o = tessOptions.Value;

    /// <summary>
    /// Pure-ish core: build the outcome given a per-zone OCR delegate. The delegate seam lets tests
    /// supply canned region results (no images, no Tesseract, no DB). Only scalar fields that have a
    /// zone are OCR'd; TABLE_CELL fields are deferred to Phase 2.
    /// </summary>
    public async Task<MappingOutcome> BuildAsync(
        MappingTemplate template,
        Func<MappingField, Task<(string Raw, decimal Conf)>> ocrZone,
        IReadOnlyDictionary<int, List<TransformerStep>> steps,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, (string Raw, decimal Conf)>();
        foreach (var field in template.Fields)
        {
            if (field.ZoneX is null) continue; // field has no zone
            if (string.Equals(field.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase)) continue;
            ct.ThrowIfCancellationRequested();
            results[field.FieldId] = await ocrZone(field);
        }
        return await mappingEngine.RunZonalAsync(template, results, steps, ct);
    }

    /// <summary>Production path: render a working raster per page, deskew once, crop + OCR each zone.</summary>
    public async Task<MappingOutcome> ProcessAsync(
        Document doc,
        MappingTemplate template,
        IReadOnlyDictionary<int, List<TransformerStep>> steps,
        CancellationToken ct = default)
    {
        var preparedPages = new Dictionary<int, Image<L8>>();
        var tempPageFiles = new List<string>();
        try
        {
            return await BuildAsync(template, async field =>
            {
                int pageNo = field.ZonePage ?? 1;
                var page = GetPreparedPage(doc, pageNo, preparedPages, tempPageFiles);

                var rect = ZoneGeometry.ToPixelRect(
                    field.ZoneX!.Value, field.ZoneY ?? 0m, field.ZoneW ?? 0m, field.ZoneH ?? 0m,
                    page.Width, page.Height);
                var (psm, whitelist) = ZoneHint.Resolve(field.ZoneOcrHint, field.ZonePsm);

                // crop the zone, then upscale toward MinOcrWidth (Lanczos) for crisp recognition
                var (tw, th) = ImagePreprocessor.ComputeTargetSize(
                    rect.Width, rect.Height, currentDpi: 0, targetDpi: _o.Dpi,
                    minOcrWidth: _o.MinOcrWidth, maxDimension: ImagePreprocessor.MaxUpscaleDimension);

                string crop = Path.Combine(Path.GetTempPath(), $"docuflow_zone_{Guid.NewGuid():N}.png");
                try
                {
                    using (var img = page.Clone(c => c
                        .Crop(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))
                        .Resize(tw, th, KnownResamplers.Lanczos3)))
                    {
                        img.SaveAsPng(crop);
                    }
                    return await regionOcr.OcrRegionAsync(crop, psm, whitelist, doc.OcrLanguages, ct);
                }
                finally
                {
                    if (File.Exists(crop)) File.Delete(crop);
                }
            }, steps, ct);
        }
        finally
        {
            foreach (var p in preparedPages.Values) p.Dispose();
            foreach (var f in tempPageFiles) if (File.Exists(f)) File.Delete(f);
        }
    }

    private Image<L8> GetPreparedPage(Document doc, int pageNo, Dictionary<int, Image<L8>> cache, List<string> temps)
    {
        if (cache.TryGetValue(pageNo, out var cached)) return cached;

        var (path, isTemp) = previewRenderer.RenderForCrop(doc.StoredPath, doc.ContentType, pageNo, _o.Dpi);
        if (isTemp) temps.Add(path);
        var prepared = preprocessor.PreparePage(path); // grayscale + deskew once
        cache[pageNo] = prepared;
        return prepared;
    }
}
