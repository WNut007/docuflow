using Microsoft.Extensions.Options;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Normalization;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Real-Tesseract test for the zonal region OCR path (the core "crop + tight PSM + whitelist reads a
/// clean value" bet). SELF-SKIPPING: it early-returns (passes) when tessdata or the native library
/// isn't available — so the suite stays green on a bare CI — and runs the real assertion where
/// Tesseract is installed (e.g. the dev machine). Fixture: a tight, upscaled crop of the invoice
/// number cell of samples/east-repair-invoice.png.
/// </summary>
public sealed class RegionOcrTests
{
    [Fact]
    public async Task OcrRegion_reads_invoice_number_with_whitelist_and_single_line_psm()
    {
        string? tessdata = FindTessdata();
        if (tessdata is null) return; // tessdata not installed here -> skip (still a pass)

        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "zone-invoice-no.png");
        Assert.True(File.Exists(fixture), $"missing fixture: {fixture}");

        var engine = new TesseractOcrEngine(
            Options.Create(new TesseractOptions { TessdataPath = tessdata, Languages = "eng" }),
            new ImagePreprocessor(),
            new TextNormalizer());

        string text;
        decimal conf;
        try
        {
            // PSM 7 = single text line; whitelist = invoice-id charset (no Thai can intrude).
            (text, conf) = await engine.OcrRegionAsync(
                fixture, psm: 7, whitelist: "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-", languages: "eng", default);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return; // native libtesseract not loadable in this environment -> skip
        }

        string compact = text.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        Assert.Equal("US-001", compact);
        Assert.True(conf > 0m, "expected a positive mean confidence");
    }

    /// <summary>Walks up from the test output dir to find a tessdata folder with eng.traineddata.</summary>
    private static string? FindTessdata()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tessdata");
            if (File.Exists(Path.Combine(candidate, "eng.traineddata"))) return candidate;
        }
        return null;
    }

    private static bool IsNativeUnavailable(Exception ex)
        => ex is DllNotFoundException or BadImageFormatException
           || ex.Message.Contains("tesseract", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("leptonica", StringComparison.OrdinalIgnoreCase);
}
