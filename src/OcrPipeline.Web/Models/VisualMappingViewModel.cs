namespace OcrPipeline.Web.Models;

/// <summary>Backing model for the point-and-click mapping screen (Views/Mapping/Visual.cshtml).</summary>
public sealed class VisualMappingViewModel
{
    public int TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string TargetModel { get; set; } = "";
    public int DocumentTypeId { get; set; }

    public long? DocumentId { get; set; }
    public int PageCount { get; set; }

    public List<VisualFieldModel> Fields { get; set; } = new();

    /// <summary>Active templates (label = "DocType — Name") for the document-type selector.</summary>
    public IReadOnlyList<TemplateOption> TemplateOptions { get; set; } = Array.Empty<TemplateOption>();

    /// <summary>Documents of this type that have previews, for the document selector.</summary>
    public IReadOnlyList<DocumentOption> Documents { get; set; } = Array.Empty<DocumentOption>();
}

public sealed record TemplateOption(int TemplateId, string Label, bool IsCurrent);
public sealed record DocumentOption(long DocumentId, string FileName, int PageCount);

public sealed class VisualFieldModel
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public bool IsRequired { get; set; }
    public string SourceType { get; set; } = "KEY_VALUE";
    public string? TableHeader { get; set; }
    public string? RowSelector { get; set; }
    public string? DefaultValue { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;

    /// <summary>Human-readable summary of the current binding (e.g. the key), never a raw regex.</summary>
    public string? BindingLabel { get; set; }

    public List<VisualSubColumnModel> SubColumns { get; set; } = new();
}

public sealed class VisualSubColumnModel
{
    public int ColumnId { get; set; }
    public string TargetSubProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public string? TableHeader { get; set; }
    public int SortOrder { get; set; }
}
