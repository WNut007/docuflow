namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Config for the Dockerized PP-Structure / PaddleOCR sidecar (see ocr-service/, docker-compose.yml).
/// The .NET app posts cropped zone rasters to <c>{BaseUrl}/ocr</c> and maps the returned word boxes
/// into the zonal pipeline. Bound from "Ocr:Paddle".
/// </summary>
public sealed class PaddleOptions
{
    /// <summary>Base URL of the OCR sidecar. Dev default matches docker-compose's published port.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Per-request timeout. PaddleOCR on a zone crop is well under this; generous for cold starts.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}
