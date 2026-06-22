using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Zonal OCR backed by the PaddleOCR sidecar (ocr-service/ <c>/ocr</c> endpoint). Drop-in alternative to
/// the Tesseract <see cref="IRegionOcrEngine"/> for the template-based path: the caller crops + upscales a
/// zone and hands us the PNG; we POST it, get back per-word text + pixel boxes + confidence, and map them
/// into <see cref="RegionWord"/> (boxes normalized 0..1 to the crop) and joined cell text.
///
/// PaddleOCR's recognizer is far stronger than Tesseract on the Michelin-class invoices (avg word conf
/// ~0.99 vs Tesseract), so its word boxes feed the existing TableRowSegmenter directly. The Tesseract-only
/// hints <c>psm</c> and <c>whitelist</c> have no PaddleOCR equivalent and are ignored.
/// </summary>
public sealed class PaddleRegionOcrEngine(
    IHttpClientFactory httpClientFactory,
    IOptions<PaddleOptions> options) : IRegionOcrEngine
{
    private readonly PaddleOptions _o = options.Value;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Cell read: OCR the crop and join its words in reading order (top-to-bottom, left-to-right) into one
    /// string; confidence is the mean word confidence (0 when the crop is blank).
    /// </summary>
    public async Task<(string Text, decimal Confidence)> OcrRegionAsync(
        string imagePath, int psm, string? whitelist, string? languages, CancellationToken ct = default)
    {
        var resp = await PostAsync(imagePath, ct);
        var words = resp.Words ?? [];
        if (words.Count == 0) return (string.Empty, 0m);

        string text = string.Join("\n", GroupReadingOrder(words).Select(line => string.Join(" ", line.Select(w => w.Text))));
        decimal conf = (decimal)words.Average(w => w.Conf);
        return (text.Trim(), conf);
    }

    /// <summary>
    /// Table geometry: OCR the whole zone crop and return per-word boxes normalized 0..1 to the crop. These
    /// drive row segmentation, so positions matter more than perfect text.
    /// </summary>
    public async Task<IReadOnlyList<RegionWord>> OcrRegionWordsAsync(
        string imagePath, int psm, string? whitelist, string? languages, CancellationToken ct = default)
    {
        var resp = await PostAsync(imagePath, ct);
        float w = resp.PageWidth, h = resp.PageHeight;
        if (w <= 0 || h <= 0 || resp.Words is null) return [];

        var words = new List<RegionWord>(resp.Words.Count);
        foreach (var word in resp.Words)
        {
            if (word.Bbox is not { Length: 4 }) continue;
            string text = (word.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            float x1 = word.Bbox[0], y1 = word.Bbox[1], x2 = word.Bbox[2], y2 = word.Bbox[3];
            words.Add(new RegionWord(text, x1 / w, y1 / h, (x2 - x1) / w, (y2 - y1) / h, (decimal)word.Conf));
        }
        return words;
    }

    // ---- helpers --------------------------------------------------------------

    private async Task<OcrResponse> PostAsync(string imagePath, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_o.TimeoutSeconds);

        await using var file = File.OpenRead(imagePath);
        using var content = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(part, "file", Path.GetFileName(imagePath));

        string url = _o.BaseUrl.TrimEnd('/') + "/ocr";
        using var resp = await client.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();

        var parsed = await resp.Content.ReadFromJsonAsync<OcrResponse>(JsonOpts, ct);
        return parsed ?? throw new InvalidOperationException($"PaddleOCR returned an empty body from {url}.");
    }

    /// <summary>Group words into reading-order lines: a new line starts when a word's vertical center clears
    /// the current line's center by more than half the line's height; within a line, sort left-to-right.</summary>
    private static List<List<OcrWord>> GroupReadingOrder(IReadOnlyList<OcrWord> words)
    {
        var ordered = words
            .Where(w => w.Bbox is { Length: 4 })
            .OrderBy(w => (w.Bbox![1] + w.Bbox[3]) / 2f)
            .ToList();

        var lines = new List<List<OcrWord>>();
        foreach (var w in ordered)
        {
            float yc = (w.Bbox![1] + w.Bbox[3]) / 2f;
            float half = (w.Bbox[3] - w.Bbox[1]) / 2f;
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line is not null)
            {
                var last = line[^1];
                float lineYc = (last.Bbox![1] + last.Bbox[3]) / 2f;
                if (Math.Abs(yc - lineYc) <= half) { line.Add(w); continue; }
            }
            lines.Add([w]);
        }

        foreach (var line in lines)
            line.Sort((a, b) => a.Bbox![0].CompareTo(b.Bbox![0]));
        return lines;
    }

    // ---- wire contract (matches ocr-service/app.py /ocr) -----------------------

    private sealed class OcrResponse
    {
        [JsonPropertyName("page_width")] public float PageWidth { get; set; }
        [JsonPropertyName("page_height")] public float PageHeight { get; set; }
        [JsonPropertyName("words")] public List<OcrWord>? Words { get; set; }
    }

    private sealed class OcrWord
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("bbox")] public float[]? Bbox { get; set; }  // [x1,y1,x2,y2] in crop pixels
        [JsonPropertyName("conf")] public float Conf { get; set; }     // 0..1
    }
}
