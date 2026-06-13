namespace OcrPipeline.Web.Models;

/// <summary>JSON body posted by the visual mapper to persist all field bindings at once.</summary>
public sealed class VisualSavePayload
{
    public int TemplateId { get; set; }
    public List<VisualSaveField> Fields { get; set; } = new();
}

public sealed class VisualSaveField
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public bool IsRequired { get; set; }
    public string SourceType { get; set; } = "KEY_VALUE";

    /// <summary>For KEY_VALUE bindings: the key text of the clicked block (server derives the pattern).</summary>
    public string? BindingKey { get; set; }
    public string? TableHeader { get; set; }   // for TABLE_CELL anchor
    public string? RowSelector { get; set; }
    public string? DefaultValue { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;

    public List<VisualSaveSubColumn> SubColumns { get; set; } = new();
}

public sealed class VisualSaveSubColumn
{
    public int ColumnId { get; set; }
    public string TargetSubProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public string? TableHeader { get; set; }
    public int SortOrder { get; set; }
}
