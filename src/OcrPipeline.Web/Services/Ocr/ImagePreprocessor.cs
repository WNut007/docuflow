using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Lightweight, managed-only image preprocessing applied before Tesseract:
/// grayscale -> ensure >= target DPI -> deskew (projection-profile) -> median denoise.
/// Separated from the engine and built from pure, unit-testable static steps.
///
/// UPGRADE PATH: for production-grade deskew/denoise (Hough/bilateral/adaptive
/// threshold) swap these steps for OpenCV (OpenCvSharp/Emgu). Kept dependency-free
/// here so the project restores and runs without native CV binaries.
/// </summary>
public sealed class ImagePreprocessor
{
    /// <summary>
    /// Preprocesses <paramref name="inputPath"/> and writes a PNG to <paramref name="outputPath"/>.
    /// </summary>
    public void Process(string inputPath, string outputPath, int targetDpi = 300, int minOcrWidth = 0)
    {
        using var image = Image.Load<Rgba32>(inputPath);

        // 1) grayscale
        image.Mutate(c => c.Grayscale());

        // 2) ensure >= target DPI, or (when density is unknown) a sensible OCR width
        EnsureDpi(image, targetDpi, minOcrWidth);

        // 3) deskew using a projection-profile angle estimate
        var gray = ToLuminance(image, out int w, out int h);
        double angle = EstimateSkewAngleDegrees(gray, w, h);
        if (Math.Abs(angle) >= 0.5)
        {
            image.Mutate(c => c.Rotate((float)-angle).BackgroundColor(Color.White));
            gray = ToLuminance(image, out w, out h);
        }

        // 4) median denoise (3x3), then write back as L8
        var denoised = Median3x3(gray, w, h);
        using var output = Image.LoadPixelData<L8>(denoised, w, h);
        CopyResolution(image.Metadata, output.Metadata);
        output.SaveAsPng(outputPath);
    }

    /// <summary>
    /// Loads a page and converts it to grayscale at its ORIGINAL size for the zonal OCR path, which
    /// then crops and upscales each drawn zone. Deliberately does NOT deskew: zones are drawn on the
    /// (un-deskewed) page preview, so the crop source must keep the same geometry — rotating here
    /// would resize/offset the canvas and desync every zone. (Skew-tolerant zonal extraction needs
    /// page registration; that is a later phase. Deskew still runs in the OCR-first <see cref="Process"/>.)
    /// </summary>
    public Image<L8> PreparePage(string inputPath)
    {
        using var image = Image.Load<Rgba32>(inputPath);
        image.Mutate(c => c.Grayscale());
        return image.CloneAs<L8>();
    }

    // ---- DPI ------------------------------------------------------------------
    /// <summary>Largest raster dimension we will ever upscale to — a guard against bogus density
    /// metadata requesting an enormous buffer (300 DPI on a US-Letter page is ~3300px).</summary>
    public const int MaxUpscaleDimension = 12_000;

    private static void EnsureDpi(Image image, int targetDpi, int minOcrWidth)
    {
        var md = image.Metadata;

        // Only an ABSOLUTE resolution unit yields a real DPI. A JFIF "aspect ratio" (units=0,
        // ResolutionUnits=AspectRatio) or any non-physical unit carries NO density — inferring
        // DPI from it is invalid (a 1:1 aspect ratio would read as 2.54 DPI and trigger a ~118x
        // upscale into a multi-gigabyte buffer). current = 0 means "density unknown".
        double current = md.ResolutionUnits switch
        {
            PixelResolutionUnit.PixelsPerInch       => md.HorizontalResolution,
            PixelResolutionUnit.PixelsPerCentimeter => md.HorizontalResolution * 2.54,
            PixelResolutionUnit.PixelsPerMeter      => md.HorizontalResolution * 0.0254,
            _                                       => 0 // AspectRatio / unknown
        };

        var (nw, nh) = ComputeTargetSize(image.Width, image.Height, current, targetDpi, minOcrWidth, MaxUpscaleDimension);
        if (nw > image.Width || nh > image.Height)
            image.Mutate(c => c.Resize(nw, nh, KnownResamplers.Lanczos3)); // Lanczos = crisp text upscale

        md.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
        md.HorizontalResolution = targetDpi;
        md.VerticalResolution = targetDpi;
    }

    /// <summary>
    /// Pure resize decision (no ImageSharp types, fully unit-testable). Chooses an upscale factor:
    /// from real density when known (raise low-DPI scans toward <paramref name="targetDpi"/>), or —
    /// when density is unknown (<paramref name="currentDpi"/> &lt; 1, e.g. a JFIF aspect-ratio image)
    /// — raise the width to <paramref name="minOcrWidth"/> so small images become legible to OCR.
    /// Never downscales; always clamps the largest side to <paramref name="maxDimension"/>.
    /// </summary>
    public static (int Width, int Height) ComputeTargetSize(
        int width, int height, double currentDpi, int targetDpi, int minOcrWidth, int maxDimension)
    {
        double scale = 1.0;
        if (currentDpi >= 1)
        {
            if (currentDpi < targetDpi) scale = targetDpi / currentDpi;
        }
        else if (minOcrWidth > 0 && width < minOcrWidth)
        {
            scale = (double)minOcrWidth / width;
        }

        if (scale < 1.0) scale = 1.0;                                       // never downscale here
        double maxScale = (double)maxDimension / Math.Max(width, height);
        if (scale > maxScale) scale = maxScale;

        int nw = Math.Max(1, (int)Math.Round(width * scale));
        int nh = Math.Max(1, (int)Math.Round(height * scale));
        return (nw, nh);
    }

    private static void CopyResolution(ImageMetadata from, ImageMetadata to)
    {
        to.ResolutionUnits = from.ResolutionUnits;
        to.HorizontalResolution = from.HorizontalResolution;
        to.VerticalResolution = from.VerticalResolution;
    }

    // ---- pure pixel helpers (unit-testable) -----------------------------------

    /// <summary>Extracts an 8-bit luminance buffer from an image.</summary>
    private static byte[] ToLuminance(Image<Rgba32> image, out int width, out int height)
    {
        width = image.Width;
        height = image.Height;
        using var l8 = image.CloneAs<L8>();
        var px = new L8[width * height];
        l8.CopyPixelDataTo(px);
        var buf = new byte[px.Length];
        for (int i = 0; i < px.Length; i++) buf[i] = px[i].PackedValue;
        return buf;
    }

    /// <summary>
    /// Estimates document skew in degrees via a projection-profile score: rows of a
    /// well-aligned page have high variance in their dark-pixel counts. Pure function.
    /// </summary>
    public static double EstimateSkewAngleDegrees(byte[] luma, int width, int height,
        double maxAngle = 5.0, double step = 0.5)
    {
        if (width <= 1 || height <= 1) return 0;

        // binarize (dark = ink) at a fixed mid threshold; downsample wide pages for speed
        int stride = Math.Max(1, width / 600);
        int bw = (width + stride - 1) / stride;
        int bh = (height + stride - 1) / stride;
        var dark = new bool[bw * bh];
        for (int y = 0, by = 0; y < height; y += stride, by++)
            for (int x = 0, bx = 0; x < width; x += stride, bx++)
                dark[by * bw + bx] = luma[y * width + x] < 128;

        double bestAngle = 0, bestScore = -1;
        for (double a = -maxAngle; a <= maxAngle + 1e-9; a += step)
        {
            double score = ProfileScore(dark, bw, bh, a);
            if (score > bestScore) { bestScore = score; bestAngle = a; }
        }
        return bestAngle;
    }

    /// <summary>Variance of per-row ink counts after shearing the binary grid by <paramref name="angleDeg"/>.</summary>
    private static double ProfileScore(bool[] dark, int w, int h, double angleDeg)
    {
        double tan = Math.Tan(angleDeg * Math.PI / 180.0);
        var rowCounts = new int[h];
        for (int y = 0; y < h; y++)
        {
            int shift = (int)Math.Round((y - h / 2.0) * tan);
            int count = 0;
            for (int x = 0; x < w; x++)
            {
                int sx = x + shift;
                if (sx >= 0 && sx < w && dark[y * w + x]) count++;
            }
            rowCounts[y] = count;
        }

        double mean = 0;
        for (int y = 0; y < h; y++) mean += rowCounts[y];
        mean /= h;
        double variance = 0;
        for (int y = 0; y < h; y++) { double d = rowCounts[y] - mean; variance += d * d; }
        return variance / h;
    }

    /// <summary>3x3 median filter over an 8-bit buffer (edge pixels copied through). Pure function.</summary>
    public static byte[] Median3x3(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        Array.Copy(src, dst, src.Length);
        var window = new byte[9];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int k = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        window[k++] = src[(y + dy) * width + (x + dx)];
                Array.Sort(window);
                dst[y * width + x] = window[4]; // median of 9
            }
        }
        return dst;
    }
}
