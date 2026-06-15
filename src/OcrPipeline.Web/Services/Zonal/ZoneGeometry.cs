namespace OcrPipeline.Web.Services.Zonal;

/// <summary>A pixel rectangle (top-left origin) — the crop region for one zone.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Pure conversion from a normalized 0..1 zone rectangle to a pixel crop within a page raster.
/// No image types so it is fully unit-testable. Clamps to the image bounds and guarantees a
/// non-empty rectangle.
/// </summary>
public static class ZoneGeometry
{
    public static PixelRect ToPixelRect(decimal x, decimal y, decimal w, decimal h, int pageWidth, int pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return new PixelRect(0, 0, 1, 1);

        int px = Clamp((int)Math.Round((double)x * pageWidth), 0, pageWidth - 1);
        int py = Clamp((int)Math.Round((double)y * pageHeight), 0, pageHeight - 1);
        int pw = Clamp((int)Math.Round((double)w * pageWidth), 1, pageWidth - px);
        int ph = Clamp((int)Math.Round((double)h * pageHeight), 1, pageHeight - py);
        return new PixelRect(px, py, pw, ph);
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
}
