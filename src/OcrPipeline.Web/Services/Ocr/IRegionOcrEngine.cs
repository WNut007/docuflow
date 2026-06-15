namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Zonal OCR seam: read text from a single pre-cropped image region with a tight Page Segmentation
/// Mode and optional character whitelist. Used by the zonal (template-based) mapping path, where the
/// user has drawn the layout, so no page-level layout analysis is wanted. Separate from
/// <see cref="IOcrEngine"/> (whole-document extraction) so each can be faked independently in tests.
/// </summary>
public interface IRegionOcrEngine
{
    /// <param name="imagePath">A cropped raster (one zone) on disk.</param>
    /// <param name="psm">Tesseract PageSegMode (e.g. 7 = single line, 8 = single word, 6 = block).</param>
    /// <param name="whitelist">Allowed characters (tessedit_char_whitelist), or null for no restriction.</param>
    /// <param name="languages">Per-call language override; null/blank uses the configured default.</param>
    /// <returns>The recognized text (trimmed) and a 0..1 mean confidence.</returns>
    Task<(string Text, decimal Confidence)> OcrRegionAsync(
        string imagePath, int psm, string? whitelist, string? languages, CancellationToken ct = default);

    /// <summary>
    /// OCR a cropped region and return per-WORD boxes (each normalized 0..1 to the crop) for GEOMETRY
    /// — table row segmentation needs word positions, not perfect text. Used with a block PSM (6) over
    /// a whole table zone; cell values are then read separately via <see cref="OcrRegionAsync"/>.
    /// </summary>
    Task<IReadOnlyList<RegionWord>> OcrRegionWordsAsync(
        string imagePath, int psm, string? whitelist, string? languages, CancellationToken ct = default);
}

/// <summary>One recognized word in a cropped region: text, box normalized 0..1 to the crop, conf 0..1.</summary>
public readonly record struct RegionWord(string Text, double X, double Y, double W, double H, decimal Conf);
