namespace OcrPipeline.Web.Services.Zonal;

/// <summary>A page-normalized rectangle (0..1 in both axes) — the zone the user drew in the designer.</summary>
public readonly record struct RectN(double X, double Y, double W, double H);

/// <summary>
/// Outcome of an Option ③-B auto-columns request: the interior column separators for ONE user-drawn zone,
/// page-normalized (0..1, ascending) so the designer can place them directly on the backdrop. The zone
/// itself is the user's input, not detected. <paramref name="Note"/> carries a user-facing message when
/// nothing was produced (detector offline / not configured / no columns found) — the designer shows it and
/// stays fully usable for manual drawing; an empty result is never an error.
/// </summary>
public sealed record ColumnDetectionResult(
    IReadOnlyList<double> PageColumnBoundariesX,
    int ColumnCount,
    string? Note)
{
    public static ColumnDetectionResult Empty(string note) => new(Array.Empty<double>(), 0, note);
}

/// <summary>
/// Engine-agnostic seam for "rough-box → auto-columns": given a user-drawn zone on a document page, propose
/// the column separators inside it. Implementations obtain table structure however they like (e.g. the
/// PaddleOCR PP-Structure sidecar) but MUST return separators in the page-normalized backdrop frame and MUST
/// degrade gracefully (return <see cref="ColumnDetectionResult.Empty"/> with a note) rather than throw when
/// their backend is unavailable. Selected by config (Ocr:TableDetect:Provider), like the OCR providers.
/// </summary>
public interface ITableLayoutDetector
{
    Task<ColumnDetectionResult> DetectColumnsAsync(long documentId, int page, RectN zone, CancellationToken ct = default);
}
