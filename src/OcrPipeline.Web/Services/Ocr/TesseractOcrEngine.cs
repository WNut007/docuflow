using System.Text.Json;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Normalization;
using Tesseract;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Real offline OCR fallback built on the Tesseract .NET wrapper (libtesseract) using the
/// LSTM engine with languages "tha+eng". Captures per-word and per-line confidence and
/// normalized (0..1) bounding boxes, runs <see cref="ImagePreprocessor"/> first, and applies
/// the shared <see cref="TextNormalizer"/> so every block carries a normalized value.
///
/// The tha/eng traineddata files are NOT bundled and are never downloaded — if the configured
/// tessdata folder is missing a language, the engine fails fast with setup guidance (see README).
/// Primary production engine remains Google Document AI (better Thai + table accuracy).
/// </summary>
public sealed class TesseractOcrEngine(
    IOptions<TesseractOptions> options,
    ImagePreprocessor preprocessor,
    TextNormalizer normalizer) : IOcrEngine
{
    private readonly TesseractOptions _o = options.Value;

    public string Name => "Tesseract";

    public Task<OcrExtraction> ExtractAsync(string filePath, string contentType, CancellationToken ct = default)
    {
        // Tesseract reads raster images. PDF rasterization to per-page PNG arrives in Prompt 2.
        if (IsPdf(filePath, contentType))
            throw new NotSupportedException(
                "TesseractOcrEngine needs a rasterized image (PNG/JPG/TIFF). PDF rasterization is added in Prompt 2; " +
                "use the Google Document AI engine for PDFs in the meantime.");

        string tessdata = ResolveTessdataOrThrow();

        // preprocess to a temporary PNG (grayscale, >=300 DPI, deskew, denoise)
        string prepped = Path.Combine(Path.GetTempPath(), $"docuflow_{Guid.NewGuid():N}.png");
        try
        {
            preprocessor.Process(filePath, prepped, _o.Dpi);

            var ex = new OcrExtraction { Engine = Name, EngineVersion = "tesseract-5/lstm", PageCount = 1 };

            using var engine = new TesseractEngine(tessdata, _o.Languages, EngineMode.LstmOnly);
            using var pix = Pix.LoadFromFile(prepped);
            using var page = engine.Process(pix);

            float imgW = pix.Width, imgH = pix.Height;
            using var iter = page.GetIterator();
            iter.Begin();

            var lineWords = new List<string>();
            do
            {
                ct.ThrowIfCancellationRequested();

                // flush the previous line when a new text line begins
                if (iter.IsAtBeginningOf(PageIteratorLevel.TextLine) && lineWords.Count > 0)
                    lineWords.Clear();

                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out Rect wr))
                {
                    string word = (iter.GetText(PageIteratorLevel.Word) ?? "").Trim();
                    if (word.Length > 0)
                    {
                        lineWords.Add(word);
                        ex.TextBlocks.Add(MakeBlock("WORD", word,
                            iter.GetConfidence(PageIteratorLevel.Word), wr, imgW, imgH));
                    }
                }

                // at the end of a text line, emit the assembled LINE block
                if (iter.IsAtFinalOf(PageIteratorLevel.TextLine, PageIteratorLevel.Word) &&
                    iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out Rect lr))
                {
                    string lineText = (iter.GetText(PageIteratorLevel.TextLine) ?? "").Trim();
                    if (lineText.Length > 0)
                        ex.TextBlocks.Add(MakeBlock("LINE", lineText,
                            iter.GetConfidence(PageIteratorLevel.TextLine), lr, imgW, imgH));
                }
            }
            while (iter.Next(PageIteratorLevel.Word));

            ApplyNormalization(ex);
            ex.RawJson = JsonSerializer.Serialize(new
            {
                engine = "tesseract",
                languages = _o.Languages,
                meanConfidence = page.GetMeanConfidence()
            });
            return Task.FromResult(ex);
        }
        finally
        {
            if (File.Exists(prepped)) File.Delete(prepped);
        }
    }

    // ---- helpers --------------------------------------------------------------

    private static bool IsPdf(string path, string contentType)
        => contentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true ||
           Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the tessdata folder and verifies each configured language is present.</summary>
    private string ResolveTessdataOrThrow()
    {
        string path = Path.IsPathRooted(_o.TessdataPath)
            ? _o.TessdataPath
            : Path.Combine(AppContext.BaseDirectory, _o.TessdataPath);

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Tesseract tessdata folder not found at '{path}'. Set Ocr:Tesseract:TessdataPath and install " +
                "tha.traineddata + eng.traineddata (LSTM 'best' models). See README \"Tesseract offline OCR setup\".");

        foreach (var lang in _o.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(Path.Combine(path, $"{lang}.traineddata")))
                throw new FileNotFoundException(
                    $"Missing '{lang}.traineddata' in '{path}'. Download the LSTM 'best' model for '{lang}' and place it " +
                    "in the tessdata folder (the file is not shipped and is never auto-downloaded). See README.");
        }
        return path;
    }

    private static OcrTextBlock MakeBlock(string type, string text, float conf0to100, Rect r, float imgW, float imgH)
        => new()
        {
            PageNumber = 1,
            BlockType = type,
            Content = text,
            Confidence = (decimal)(conf0to100 / 100f),
            BBoxLeft = imgW > 0 ? (decimal)(r.X1 / imgW) : null,
            BBoxTop = imgH > 0 ? (decimal)(r.Y1 / imgH) : null,
            BBoxWidth = imgW > 0 ? (decimal)(r.Width / imgW) : null,
            BBoxHeight = imgH > 0 ? (decimal)(r.Height / imgH) : null
        };

    /// <summary>Infers the document's day/month order once, then normalizes every block's text.</summary>
    private void ApplyNormalization(OcrExtraction ex)
    {
        var order = normalizer.InferDayMonthOrder(ex.TextBlocks.Select(b => b.Content));
        foreach (var b in ex.TextBlocks)
            b.NormalizedContent = normalizer.Normalize(b.Content, order).Normalized;
    }
}
