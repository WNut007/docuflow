namespace OcrPipeline.Web.Models;

/// <summary>Backing model for the upload form: the user must pick which template/layout to use
/// (manual + required — the pipeline does not guess by page count).</summary>
public sealed class UploadViewModel
{
    public IReadOnlyList<UploadTemplateOption> Templates { get; init; } = Array.Empty<UploadTemplateOption>();
}

public sealed record UploadTemplateOption(int TemplateId, string Label);
