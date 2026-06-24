namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Default <see cref="ITableLayoutDetector"/> when no table detector is configured
/// (Ocr:TableDetect:Provider = None, the shipped default). Auto-detect is wired in the designer but
/// returns nothing with an explanatory note, so an unconfigured deployment behaves exactly as before:
/// the button is present, the designer is fully usable, and the user draws columns by hand.
/// </summary>
public sealed class NullTableLayoutDetector : ITableLayoutDetector
{
    public Task<ColumnDetectionResult> DetectColumnsAsync(long documentId, int page, RectN zone, CancellationToken ct = default)
        => Task.FromResult(ColumnDetectionResult.Empty("Auto-detect is not enabled on this server."));
}
