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
    public IReadOnlyList<DocumentOption> Documents { get; set; } = Array.Empty<DocumentOption>();
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
}

// ---- save payload (JSON POST from zone-designer.js) ----
public sealed class ZonesSavePayload
{
    public int TemplateId { get; set; }
    public string MappingMode { get; set; } = "OCR_FIRST";
    public List<ZoneFieldPayload> Fields { get; set; } = new();
}

public sealed class ZoneFieldPayload
{
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public bool IsRequired { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;

    public int? ZonePage { get; set; }
    public decimal? ZoneX { get; set; }
    public decimal? ZoneY { get; set; }
    public decimal? ZoneW { get; set; }
    public decimal? ZoneH { get; set; }
    public string? ZoneOcrHint { get; set; }
    public byte? ZonePsm { get; set; }
}
