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

    /// <summary>Runs OCR on a stored file and returns text blocks + tables.</summary>
    Task<OcrExtraction> ExtractAsync(string filePath, string contentType, CancellationToken ct = default);
}
