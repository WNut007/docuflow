namespace OcrPipeline.Web.Models;

/// <summary>Backing model for the zone designer (Views/Mapping/Zones.cshtml).</summary>
public sealed class ZoneDesignerViewModel
{
    public int TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string MappingMode { get; set; } = "OCR_FIRST";
    public int DocumentTypeId { get; set; }

    public long? DocumentId { get; set; }
    public int PageCount { get; set; }

    public List<ZoneFieldModel> Fields { get; set; } = new();
    public IReadOnlyList<TemplateOption> TemplateOptions { get; set; } = Array.Empty<TemplateOption>();
}

public sealed class ZoneFieldModel
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public bool IsRequired { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;
    public string SourceType { get; set; } = "KEY_VALUE";

    public int? ZonePage { get; set; }
    public decimal? ZoneX { get; set; }
    public decimal? ZoneY { get; set; }
    public decimal? ZoneW { get; set; }
    public decimal? ZoneH { get; set; }
    public string? ZoneOcrHint { get; set; }
    public byte? ZonePsm { get; set; }

    /// <summary>Multi-page page-role (Phase 3): FIRST/CONTINUATION/LAST; null = single-page/legacy.</summary>
    public string? ZonePageRole { get; set; }

    /// <summary>Sub-columns when this is the line_item table field (SourceType == TABLE_CELL).</summary>
    public List<ZoneColumnModel> Columns { get; set; } = new();
}

/// <summary>One table column for the designer: its sub-field, x-boundaries, anchor flag, and line rule.</summary>
public sealed class ZoneColumnModel
{
    public int ColumnId { get; set; }
    public string TargetSubProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public int SortOrder { get; set; }
    public decimal? ColXStart { get; set; }
    public decimal? ColXEnd { get; set; }
    public bool IsAnchor { get; set; }
    public string? LineSelectMode { get; set; }
    public string? LineSelectIndices { get; set; }
    public string? LineJoinSeparator { get; set; }
}

// ---- save payload (JSON POST from zone-designer.js) ----
public sealed class ZonesSavePayload
{
    public int TemplateId { get; set; }
    public string MappingMode { get; set; } = "OCR_FIRST";
    public List<ZoneFieldPayload> Fields { get; set; } = new();
    /// <summary>Saved fields the user removed in the designer (e.g. a redundant page-region). Deleted
    /// FK-safely — a field still referenced by a stored extraction result is left untouched.</summary>
    public List<int> RemovedFieldIds { get; set; } = new();
}

public sealed class ZoneFieldPayload
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public bool IsRequired { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;
    public string SourceType { get; set; } = "KEY_VALUE";   // TABLE_CELL for the line_item table field

    public int? ZonePage { get; set; }
    public decimal? ZoneX { get; set; }
    public decimal? ZoneY { get; set; }
    public decimal? ZoneW { get; set; }
    public decimal? ZoneH { get; set; }
    public string? ZoneOcrHint { get; set; }
    public byte? ZonePsm { get; set; }
    public string? ZonePageRole { get; set; }   // FIRST/CONTINUATION/LAST (Phase 3)

    public List<ZoneColumnPayload> Columns { get; set; } = new();
}

public sealed class ZoneColumnPayload
{
    public int ColumnId { get; set; }
    public string TargetSubProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public int SortOrder { get; set; }
    public decimal? ColXStart { get; set; }
    public decimal? ColXEnd { get; set; }
    public bool IsAnchor { get; set; }
    public string? LineSelectMode { get; set; }
    public string? LineSelectIndices { get; set; }
    public string? LineJoinSeparator { get; set; }
}
