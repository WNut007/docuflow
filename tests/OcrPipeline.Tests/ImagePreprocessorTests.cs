using OcrPipeline.Web.Services.Ocr;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests for the pure resize decision used before Tesseract. Covers the bug that sent a
/// JFIF aspect-ratio image (no real DPI) into a multi-gigabyte upscale, and the new min-OCR-width
/// floor that sharpens small images for recognition.
/// </summary>
public sealed class ImagePreprocessorTests
{
    private const int MaxDim = 12_000;

    [Fact]
    public void Unknown_density_upscales_to_min_width()
    {
        // east-repair-invoice.png: 750x1061, JFIF units=0 -> currentDpi unknown (0)
        var (w, h) = ImagePreprocessor.ComputeTargetSize(750, 1061, currentDpi: 0, targetDpi: 300, minOcrWidth: 2200, MaxDim);
        Assert.Equal(2200, w);
        Assert.Equal(3112, h);                 // aspect preserved: round(1061 * 2200/750) = round(3112.27)
    }

    [Fact]
    public void Unknown_density_with_no_floor_leaves_image_unchanged()
    {
        // This is the crash guard: without a width floor, an unknown density must NOT upscale.
        var (w, h) = ImagePreprocessor.ComputeTargetSize(750, 1061, currentDpi: 0, targetDpi: 300, minOcrWidth: 0, MaxDim);
        Assert.Equal(750, w);
        Assert.Equal(1061, h);
    }

    [Fact]
    public void Unknown_density_already_wide_enough_is_not_upscaled()
    {
        var (w, h) = ImagePreprocessor.ComputeTargetSize(2600, 3400, currentDpi: 0, targetDpi: 300, minOcrWidth: 2200, MaxDim);
        Assert.Equal(2600, w);
        Assert.Equal(3400, h);
    }

    [Fact]
    public void Known_low_dpi_scales_toward_target()
    {
        // 150 DPI scan -> 300 DPI target = 2x (the width floor does not apply when density is known)
        var (w, h) = ImagePreprocessor.ComputeTargetSize(1000, 1300, currentDpi: 150, targetDpi: 300, minOcrWidth: 2200, MaxDim);
        Assert.Equal(2000, w);
        Assert.Equal(2600, h);
    }

    [Fact]
    public void Known_sufficient_dpi_is_not_changed()
    {
        var (w, h) = ImagePreprocessor.ComputeTargetSize(2480, 3508, currentDpi: 300, targetDpi: 300, minOcrWidth: 2200, MaxDim);
        Assert.Equal(2480, w);
        Assert.Equal(3508, h);
    }

    [Fact]
    public void Scale_is_capped_at_max_dimension()
    {
        // Tiny image, huge implied scale -> clamp the longest side to MaxDim, never beyond.
        var (w, h) = ImagePreprocessor.ComputeTargetSize(100, 200, currentDpi: 0, targetDpi: 300, minOcrWidth: 50_000, MaxDim);
        Assert.Equal(MaxDim, h);               // height is the longest side
        Assert.Equal(6000, w);                 // 100 * (12000/200)
    }
}
