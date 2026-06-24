using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Zonal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Option ③-B auto-columns via the PaddleOCR PP-Structure sidecar. The user draws a rough table zone; we
/// render the page to the SAME 200-DPI non-deskewed preview the designer shows, crop that zone, POST the
/// crop to <c>/structure</c>, and turn the returned per-cell pixel boxes into page-normalized column
/// separators. PP-Structure is strong on COLUMNS for a tight crop and unreliable on vertical extent for a
/// full page (proven from real captures), so ③-B only ever asks it about columns inside a crop the user
/// already bounded — never the whole page.
///
/// Coordinate frame: the crop is a sub-rectangle of the non-deskewed preview, so pixel/page_width|height
/// gives crop-normalized 0..1 and <see cref="TableLayoutGeometry.CropXToPageX"/> (offset+scale by the zone)
/// gives page-normalized — landing on the backdrop by construction, no deskew term.
///
/// Degradation: if the sidecar is unreachable it returns an EMPTY result with a note (the designer stays
/// usable for manual drawing). It never throws on a sidecar outage and never falls back to Tesseract —
/// Tesseract has no table structure, so a fallback would be garbage (unlike the region-OCR engine).
/// </summary>
public sealed class PaddleStructureTableDetector(
    IDocumentRepository documents,
    PagePreviewRenderer previewRenderer,
    IHttpClientFactory httpClientFactory,
    IOptions<PaddleOptions> paddleOptions,
    ILogger<PaddleStructureTableDetector> logger) : ITableLayoutDetector
{
    private readonly PaddleOptions _o = paddleOptions.Value;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Crop-tuned column options: re-validated against the real short Michelin crops — floor=4 yields the 6
    // logical columns there, while a lower floor over-splits (re-admits a description sub-cluster). Kept
    // identical to the geometry default; surfaced here so a future config can tune per deployment.
    private static readonly TableLayoutGeometry.ColumnOptions ColumnOpts = new();

    public async Task<ColumnDetectionResult> DetectColumnsAsync(
        long documentId, int page, RectN zone, CancellationToken ct = default)
    {
        var doc = documents.GetById(documentId);
        if (doc is null) return ColumnDetectionResult.Empty("Document not found.");
        if (zone.W <= 0 || zone.H <= 0) return ColumnDetectionResult.Empty("Draw a table zone first, then Auto-detect.");

        // Render to the backdrop's preview frame if it isn't already on disk (the designer backdrop usually
        // put it there), then crop the drawn zone to a temp PNG to post.
        string previewPath = PagePreviewRenderer.PreviewPath(doc.StoredPath, page);
        if (!File.Exists(previewPath))
        {
            previewRenderer.Render(doc.StoredPath, doc.ContentType, dpi: 200);
            if (!File.Exists(previewPath))
                return ColumnDetectionResult.Empty("Page preview is not available yet.");
        }

        string cropPath = Path.Combine(Path.GetTempPath(), $"docuflow_detect_{Guid.NewGuid():N}.png");
        try
        {
            using (var img = await Image.LoadAsync<Rgba32>(previewPath, ct))
            {
                var rect = ZoneGeometry.ToPixelRect(
                    (decimal)zone.X, (decimal)zone.Y, (decimal)zone.W, (decimal)zone.H, img.Width, img.Height);
                if (rect.Width < 1 || rect.Height < 1)
                    return ColumnDetectionResult.Empty("Zone is too small to detect columns.");
                img.Mutate(c => c.Crop(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height)));
                await img.SaveAsPngAsync(cropPath, ct);
            }

            StructureResponse? resp;
            try
            {
                resp = await PostStructureAsync(cropPath, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex,
                    "PP-Structure sidecar unavailable ({BaseUrl}); auto-detect returns no columns (manual drawing unaffected).", _o.BaseUrl);
                return ColumnDetectionResult.Empty("Auto-detect unavailable — the table detector service is offline.");
            }

            var pageBoundaries = CropCellsToPageBoundaries(
                Flatten(resp), resp?.PageWidth ?? 0, resp?.PageHeight ?? 0, zone, ColumnOpts);

            return pageBoundaries.Count == 0
                ? ColumnDetectionResult.Empty("No columns detected — draw them manually.")
                : new ColumnDetectionResult(pageBoundaries, pageBoundaries.Count + 1, Note: null);
        }
        finally
        {
            if (File.Exists(cropPath)) File.Delete(cropPath);
        }
    }

    /// <summary>
    /// PURE map: per-cell PIXEL boxes (crop frame) → page-normalized interior column separators. Normalizes
    /// by the crop dimensions (pixel/page_width|height → 0..1), clusters columns via the engine-agnostic
    /// <see cref="TableLayoutGeometry"/>, then projects from crop to page through the drawn zone. No I/O —
    /// unit-tested against real captured crop cells. Cell boxes that aren't 4-length are skipped.
    /// </summary>
    public static IReadOnlyList<double> CropCellsToPageBoundaries(
        IReadOnlyList<double[]> cellBoxesPx, int cropWidth, int cropHeight, RectN zone,
        TableLayoutGeometry.ColumnOptions? options = null)
    {
        if (cellBoxesPx is null || cellBoxesPx.Count == 0 || cropWidth <= 0 || cropHeight <= 0)
            return Array.Empty<double>();

        var cells = new List<TableLayoutGeometry.CellBox>(cellBoxesPx.Count);
        foreach (var b in cellBoxesPx)
        {
            if (b is not { Length: 4 }) continue;
            cells.Add(new TableLayoutGeometry.CellBox(
                b[0] / cropWidth, b[1] / cropHeight, b[2] / cropWidth, b[3] / cropHeight));
        }

        var cropBoundaries = TableLayoutGeometry.DetectColumnBoundaries(cells, options);
        return TableLayoutGeometry.ToPageX(cropBoundaries, zone.X, zone.W);
    }

    // ---- helpers --------------------------------------------------------------

    private static IReadOnlyList<double[]> Flatten(StructureResponse? resp)
        => resp?.Tables is null
            ? Array.Empty<double[]>()
            : resp.Tables.Where(t => t.CellBbox is not null).SelectMany(t => t.CellBbox!).ToList();

    private async Task<StructureResponse> PostStructureAsync(string imagePath, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_o.TimeoutSeconds);

        await using var file = File.OpenRead(imagePath);
        using var content = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(part, "file", Path.GetFileName(imagePath));

        string url = _o.BaseUrl.TrimEnd('/') + "/structure";
        using var resp = await client.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<StructureResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException($"PP-Structure returned an empty body from {url}.");
    }

    // ---- wire contract (matches ocr-service/app.py /structure; html + region_bbox ignored) -------------

    private sealed class StructureResponse
    {
        [JsonPropertyName("page_width")] public int PageWidth { get; set; }
        [JsonPropertyName("page_height")] public int PageHeight { get; set; }
        [JsonPropertyName("tables")] public List<StructTable>? Tables { get; set; }
    }

    private sealed class StructTable
    {
        [JsonPropertyName("cell_bbox")] public double[][]? CellBbox { get; set; }  // per-cell [x1,y1,x2,y2] pixels
    }
}
