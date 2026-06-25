using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// PaddleRegionOcrEngine degrades to Tesseract when the sidecar is unreachable (the gate for ever making
/// Paddle the committed default). We prove DELEGATION without installed tessdata: the fallback Tesseract
/// engine is pointed at a non-existent tessdata folder, so reaching it throws a DISTINCT
/// DirectoryNotFoundException ("...tessdata folder not found...") — seeing that, rather than the sidecar's
/// HttpRequestException, proves control crossed into the fallback. A genuine caller cancellation must NOT
/// fall back; it propagates.
/// </summary>
public sealed class PaddleRegionOcrFallbackTests
{
    [Fact]
    public async Task OcrRegionAsync_falls_back_to_Tesseract_when_the_sidecar_is_unreachable()
    {
        var (engine, file) = NewEngine(ct => new HttpRequestException("connection refused (test)"));
        try
        {
            var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(
                () => engine.OcrRegionAsync(file, psm: 6, whitelist: null, languages: "eng"));
            Assert.Contains("tessdata", ex.Message, StringComparison.OrdinalIgnoreCase);   // reached Tesseract
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task OcrRegionWordsAsync_falls_back_to_Tesseract_when_the_sidecar_is_unreachable()
    {
        var (engine, file) = NewEngine(ct => new HttpRequestException("connection refused (test)"));
        try
        {
            var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(
                () => engine.OcrRegionWordsAsync(file, psm: 6, whitelist: null, languages: "eng"));
            Assert.Contains("tessdata", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_fall_back()
    {
        // The handler raises a cancellation (as HttpClient does when the caller's token trips); because the
        // token is cancelled, IsSidecarFailure returns false, so it propagates instead of degrading. If the
        // gate were wrong we'd instead see the fallback's DirectoryNotFoundException.
        var (engine, file) = NewEngine(ct => new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.OcrRegionAsync(file, psm: 6, whitelist: null, languages: "eng", cts.Token));
        }
        finally { File.Delete(file); }
    }

    // ---- fixtures -------------------------------------------------------------

    /// <summary>A PaddleRegionOcrEngine whose HTTP send always fails via <paramref name="fail"/>, wired to a
    /// Tesseract fallback that points at a guaranteed-missing tessdata folder. Returns a real temp file so
    /// PostAsync's File.OpenRead succeeds and the request actually reaches the failing handler.</summary>
    private static (PaddleRegionOcrEngine Engine, string File) NewEngine(Func<CancellationToken, Exception> fail)
    {
        var tess = new TesseractOcrEngine(
            Options.Create(new TesseractOptions
            {
                TessdataPath = Path.Combine(Path.GetTempPath(), "docuflow_no_tessdata_" + Guid.NewGuid().ToString("N")),
                Languages = "eng"
            }),
            new ImagePreprocessor(),
            new TextNormalizer());

        var factory = new StubHttpClientFactory(new FailingHandler(fail));
        var engine = new PaddleRegionOcrEngine(factory, tess, Options.Create(new PaddleOptions()),
            NullLogger<PaddleRegionOcrEngine>.Instance);

        string file = Path.Combine(Path.GetTempPath(), "docuflow_paddle_fb_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47 });   // content irrelevant; the send fails first
        return (engine, file);
    }

    private sealed class FailingHandler(Func<CancellationToken, Exception> fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(fail(ct));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
