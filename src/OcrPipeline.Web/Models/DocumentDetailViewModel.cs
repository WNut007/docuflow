using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Models;

public sealed class DocumentDetailViewModel
{
    public required Document Document { get; init; }
    public OcrExtraction? Ocr { get; init; }
    public IReadOnlyList<DocumentProperty> Properties { get; init; } = Array.Empty<DocumentProperty>();

    public decimal? MappingConfidence { get; init; }
    public bool MappingNeedsReview { get; init; }
    public string? MappedJson { get; init; }
    public IReadOnlyList<MappedValueRow> MappedValues { get; init; } = Array.Empty<MappedValueRow>();
}
