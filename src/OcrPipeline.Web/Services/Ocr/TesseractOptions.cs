namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Bound from configuration section "Ocr:Tesseract".
/// The traineddata files are NOT shipped with the NuGet package and are never
/// auto-downloaded — see the README for how to install tha/eng LSTM "best" models.
/// </summary>
public sealed class TesseractOptions
{
    /// <summary>Folder containing tha.traineddata / eng.traineddata (the "tessdata" directory).</summary>
    public string TessdataPath { get; set; } = "tessdata";

    /// <summary>Tesseract language string. Thai + English by default.</summary>
    public string Languages { get; set; } = "tha+eng";

    /// <summary>Target DPI the preprocessor upscales to before recognition (Tesseract likes >= 300).</summary>
    public int Dpi { get; set; } = 300;

    /// <summary>
    /// When the source carries no physical DPI (e.g. a JFIF "aspect ratio" image), upscale so the
    /// image is at least this many pixels wide before OCR. 0 disables the floor.
    /// ~2200px ≈ 300 DPI across a ~7.3in-wide page — small scans/screenshots OCR far better at this size.
    /// </summary>
    public int MinOcrWidth { get; set; } = 2200;
}
