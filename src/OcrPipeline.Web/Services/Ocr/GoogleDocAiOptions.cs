namespace OcrPipeline.Web.Services.Ocr;

/// <summary>Bound from configuration section "Ocr:GoogleDocAi".</summary>
public sealed class GoogleDocAiOptions
{
    public string ProjectId { get; set; } = "";
    public string Location { get; set; } = "us";          // "us" or "eu"
    public string ProcessorId { get; set; } = "";
    public string? ProcessorVersion { get; set; }          // optional pinned version
    /// <summary>Online ProcessDocument page limit (Form Parser is ~15). Bigger files need batch via GCS.</summary>
    public int OnlinePageLimit { get; set; } = 15;
}
