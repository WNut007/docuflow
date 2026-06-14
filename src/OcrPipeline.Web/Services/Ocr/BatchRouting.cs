namespace OcrPipeline.Web.Services.Ocr;

/// <summary>Pure, testable decision for whether a Document AI request should go to batch (GCS).</summary>
public static class BatchRouting
{
    /// <summary>
    /// Batch is used only when the page count exceeds the online limit AND a GCS bucket is
    /// configured. (Over the limit with no bucket is a configuration error the engine surfaces.)
    /// </summary>
    public static bool ShouldBatch(int pageCount, GoogleDocAiOptions o)
        => pageCount > o.OnlinePageLimit && !string.IsNullOrWhiteSpace(o.Bucket);
}
