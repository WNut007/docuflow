namespace OcrPipeline.Web.Models;

public sealed class TemplateEditViewModel
{
    public int TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string TargetModel { get; set; } = "";
    public int DocumentTypeId { get; set; }
    public List<FieldEditModel> Fields { get; set; } = new();

    // available property keys discovered from processed documents (for datalist)
    public IReadOnlyList<string> PropertyKeys { get; set; } = Array.Empty<string>();
}

public sealed class FieldEditModel
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public bool IsRequired { get; set; }
    public string SourceType { get; set; } = "KEY_VALUE";
    public string? KeyPattern { get; set; }
    public string? SourcePattern { get; set; }
    public string? TableHeader { get; set; }
    public string? RowSelector { get; set; }
    public string? DefaultValue { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;

    /// <summary>One step per line: "type|configJson" (configJson optional).</summary>
    public string? StepsText { get; set; }
}
