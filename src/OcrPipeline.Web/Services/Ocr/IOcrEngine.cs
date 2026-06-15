using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Contract every OCR provider implements. Swap the registration in Program.cs
/// to switch between Tesseract (local), Google Document AI, Azure Form Recognizer
/// or AWS Textract without touching the rest of the pipeline.
/// </summary>
public interface IOcrEngine
{
    string Name { get; }

    /// <summary>
    /// Runs OCR on a stored file and returns text blocks + tables. <paramref name="languages"/> is an
    /// optional per-document override (e.g. "eng" or "tha+eng"); null/blank uses the engine's configured
    /// default. Engines that auto-detect language (Google Document AI) may ignore it.
    /// </summary>
    Task<OcrExtraction> ExtractAsync(string filePath, string contentType, string? languages = null, CancellationToken ct = default);
}
