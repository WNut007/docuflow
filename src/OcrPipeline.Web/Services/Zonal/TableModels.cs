namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// One OCR word inside a table zone, box normalized 0..1 RELATIVE TO THE ZONE (not the page):
/// X/Y top-left, W/H size, Conf 0..1. Pure data produced by the region OCR pass and consumed by
/// <see cref="TableRowSegmenter"/> — no image types, so the segmenter is fully unit-testable.
/// </summary>
public readonly record struct WordBox(string Text, double X, double Y, double W, double H, decimal Conf)
{
    public double XCenter => X + W / 2.0;
    public double YCenter => Y + H / 2.0;
    public double YBottom => Y + H;
}

/// <summary>A detected row's vertical band, normalized 0..1 relative to the table zone (top origin).</summary>
public readonly record struct RowBand(double YStart, double YEnd)
{
    public double Height => YEnd - YStart;
}
