using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OcrPipeline.Web.Services.Imaging;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// MEASUREMENT harness (not a validation): renders every page of samples/doc_rt.pdf at the 200-DPI
/// designer frame, posts each whole page to the running PaddleOCR sidecar (/ocr), and dumps every word
/// with its PAGE-NORMALIZED bbox so we can locate the ground-truth fields and derive manual zone rects.
/// Output goes to the scratchpad dir (env DOCRT_OUT) as JSON + a flat text listing. Self-skips when the
/// sample or the sidecar is absent.
/// </summary>
public sealed class DocRtMeasure
{
    [Fact]
    public async Task Dump_full_page_ocr_for_doc_rt()
    {
        string sampleName = Environment.GetEnvironmentVariable("DOCRT_SAMPLE") ?? "doc_rt.pdf";
        string stem = Path.GetFileNameWithoutExtension(sampleName);
        string? sample = FindUp(Path.Combine("samples", sampleName));
        if (sample is null) { return; }

        string outDir = Environment.GetEnvironmentVariable("DOCRT_OUT")
            ?? Path.Combine(Path.GetTempPath(), "docrt_measure");
        Directory.CreateDirectory(outDir);

        var pages = new PagePreviewRenderer().Render(sample, "application/pdf", 200);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var listing = new StringBuilder();
        var jsonPages = new List<object>();

        foreach (var p in pages)
        {
            string png = PagePreviewRenderer.PreviewPath(sample, p.PageNumber);
            File.Copy(png, Path.Combine(outDir, $"{stem}.page-{p.PageNumber}.png"), overwrite: true);

            using var content = new MultipartFormDataContent();
            await using var fs = File.OpenRead(png);
            var part = new StreamContent(fs);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(part, "file", Path.GetFileName(png));

            using var resp = await http.PostAsync("http://localhost:8080/ocr", content);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            float pw = root.GetProperty("page_width").GetSingle();
            float ph = root.GetProperty("page_height").GetSingle();

            listing.AppendLine($"=== PAGE {p.PageNumber}  pixels={pw}x{ph}  (designer dims {p.Width}x{p.Height}) ===");
            var words = new List<object>();
            foreach (var w in root.GetProperty("words").EnumerateArray())
            {
                string text = w.GetProperty("text").GetString() ?? "";
                var b = w.GetProperty("bbox");
                float x1 = b[0].GetSingle(), y1 = b[1].GetSingle(), x2 = b[2].GetSingle(), y2 = b[3].GetSingle();
                float conf = w.GetProperty("conf").GetSingle();
                // page-normalized 0..1
                double nx = x1 / pw, ny = y1 / ph, nw = (x2 - x1) / pw, nh = (y2 - y1) / ph;
                listing.AppendLine(
                    $"x={nx:F4} y={ny:F4} w={nw:F4} h={nh:F4} conf={conf:F3}  |{text}|");
                words.Add(new { text, x = nx, y = ny, w = nw, h = nh, conf });
            }
            jsonPages.Add(new { page = p.PageNumber, page_width = pw, page_height = ph, words });
        }

        File.WriteAllText(Path.Combine(outDir, $"{stem}_words.txt"), listing.ToString());
        File.WriteAllText(Path.Combine(outDir, $"{stem}_words.json"),
            JsonSerializer.Serialize(jsonPages, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
