namespace OcrPipeline.Web.Services.Mapping;

public sealed class MappedValue
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string? RawValue { get; set; }
    public string? NormalizedValue { get; set; }
    public decimal? Confidence { get; set; }
    public string? SourceRef { get; set; }
    public bool IsBelowThreshold { get; set; }
}

public sealed class MappingOutcome
{
    public int TemplateId { get; set; }
    public string TargetModel { get; set; } = "";
    public decimal? OverallConfidence { get; set; }
    public bool NeedsReview { get; set; }
    public string MappedJson { get; set; } = "{}";
    public List<MappedValue> Values { get; set; } = new();
}
