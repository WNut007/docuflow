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
    TextNormalizer normalizer) : IOcrEngine, IRegionOcrEngine
{
    private readonly TesseractOptions _o = options.Value;

    public string Name => "Tesseract";

    /// <summary>
    /// Zonal OCR: read one already-cropped region with a tight PageSegMode and optional character
    /// whitelist. No preprocessing/layout analysis here — the caller has cropped (and upscaled) the
    /// zone; the human supplied the layout. Reuses the effective-language fallback + tessdata check.
    /// </summary>
    public Task<(string Text, decimal Confidence)> OcrRegionAsync(
        string imagePath, int psm, string? whitelist, string? languages, CancellationToken ct = default)
    {
        string langs = string.IsNullOrWhiteSpace(languages) ? _o.Languages : languages.Trim();
        string tessdata = ResolveTessdataOrThrow(langs);

        using var engine = new TesseractEngine(tessdata, langs, EngineMode.LstmOnly);
        if (!string.IsNullOrEmpty(whitelist))
            engine.SetVariable("tessedit_char_whitelist", whitelist);

        using var pix = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(pix, (PageSegMode)psm);

        string text = (page.GetText() ?? "").Trim();
        decimal conf = (decimal)(page.GetMeanConfidence()); // 0..1
        return Task.FromResult((text, conf));
    }

    /// <summary>
    /// Zonal table geometry: OCR a cropped region (typically a whole table zone, block PSM) and return
    /// per-word boxes normalized 0..1 to the crop. Reuses the same word iterator as the full-page path
    /// (<see cref="ExtractAsync"/>). Text is incidental here — the boxes drive row segmentation.
    /// </summary>
    public Task<IReadOnlyList<RegionWord>> OcrRegionWordsAsync(
        string imagePath, int psm, string? whitelist, string? languages, CancellationToken ct = default)
    {
        string langs = string.IsNullOrWhiteSpace(languages) ? _o.Languages : languages.Trim();
        string tessdata = ResolveTessdataOrThrow(langs);

        using var engine = new TesseractEngine(tessdata, langs, EngineMode.LstmOnly);
        if (!string.IsNullOrEmpty(whitelist))
            engine.SetVariable("tessedit_char_whitelist", whitelist);

        using var pix = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(pix, (PageSegMode)psm);

        float imgW = pix.Width, imgH = pix.Height;
        var words = new List<RegionWord>();
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            ct.ThrowIfCancellationRequested();
            if (imgW > 0 && imgH > 0 && iter.TryGetBoundingBox(PageIteratorLevel.Word, out Rect wr))
            {
                string word = (iter.GetText(PageIteratorLevel.Word) ?? "").Trim();
                if (word.Length > 0)
                    words.Add(new RegionWord(word,
                        wr.X1 / imgW, wr.Y1 / imgH, wr.Width / imgW, wr.Height / imgH,
                        (decimal)(iter.GetConfidence(PageIteratorLevel.Word) / 100f)));
            }
        }
        while (iter.Next(PageIteratorLevel.Word));
        return Task.FromResult<IReadOnlyList<RegionWord>>(words);
    }

    public Task<OcrExtraction> ExtractAsync(string filePath, string contentType, string? languages = null, CancellationToken ct = default)
    {
        // Tesseract reads raster images. PDF rasterization to per-page PNG arrives in Prompt 2.
        if (IsPdf(filePath, contentType))
            throw new NotSupportedException(
                "TesseractOcrEngine needs a rasterized image (PNG/JPG/TIFF). PDF rasterization is added in Prompt 2; " +
                "use the Google Document AI engine for PDFs in the meantime.");

        // Per-document language override (e.g. "eng" for a Latin-only invoice) falls back to config default.
        string langs = string.IsNullOrWhiteSpace(languages) ? _o.Languages : languages.Trim();
        string tessdata = ResolveTessdataOrThrow(langs);

        // preprocess to a temporary PNG (grayscale, ensure DPI / min OCR width, deskew, denoise)
        string prepped = Path.Combine(Path.GetTempPath(), $"docuflow_{Guid.NewGuid():N}.png");
        try
        {
            preprocessor.Process(filePath, prepped, _o.Dpi, _o.MinOcrWidth);

            var ex = new OcrExtraction { Engine = Name, EngineVersion = "tesseract-5/lstm", PageCount = 1 };

            using var engine = new TesseractEngine(tessdata, langs, EngineMode.LstmOnly);
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
                languages = langs,
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

    /// <summary>Resolves the tessdata folder and verifies each requested language is present.</summary>
    private string ResolveTessdataOrThrow(string languages)
    {
        string path = Path.IsPathRooted(_o.TessdataPath)
            ? _o.TessdataPath
            : Path.Combine(AppContext.BaseDirectory, _o.TessdataPath);

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Tesseract tessdata folder not found at '{path}'. Set Ocr:Tesseract:TessdataPath and install " +
                "tha.traineddata + eng.traineddata (LSTM 'best' models). See README \"Tesseract offline OCR setup\".");

        foreach (var lang in languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
