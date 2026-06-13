namespace OcrPipeline.Web.Domain;

public sealed class AppUser
{
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; }
}

public sealed class Document
{
    public long DocumentId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string SourceChannel { get; set; } = "UPLOAD";
    public int? DocumentTypeId { get; set; }
    public decimal? ClassifyConfidence { get; set; }
    public string StatusCode { get; set; } = "CAPTURED";
    public int PageCount { get; set; }
    public int? UploadedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class OcrRun
{
    public long OcrRunId { get; set; }
    public long DocumentId { get; set; }
    public string Engine { get; set; } = "";
    public string? EngineVersion { get; set; }
    public bool Succeeded { get; set; }
    public string? RawJson { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class OcrTextBlock
{
    public long TextBlockId { get; set; }
    public long OcrRunId { get; set; }
    public int PageNumber { get; set; }
    public string BlockType { get; set; } = "LINE";   // LINE/PARAGRAPH/KEY/VALUE/WORD
    public string Content { get; set; } = "";
    public string? NormalizedContent { get; set; }     // normalized form (Thai digits/number/date); null when same as Content
    public decimal? Confidence { get; set; }
    public decimal? BBoxLeft { get; set; }
    public decimal? BBoxTop { get; set; }
    public decimal? BBoxWidth { get; set; }
    public decimal? BBoxHeight { get; set; }
    public long? PairedWithId { get; set; }
}

public sealed class OcrTable
{
    public long OcrTableId { get; set; }
    public long OcrRunId { get; set; }
    public int PageNumber { get; set; }
    public int TableIndex { get; set; }
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public decimal? Confidence { get; set; }
    public List<OcrTableCell> Cells { get; set; } = new();
}

public sealed class OcrTableCell
{
    public long OcrTableCellId { get; set; }
    public long OcrTableId { get; set; }
    public int RowIndex { get; set; }
    public int ColIndex { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
    public bool IsHeader { get; set; }
    public string? Content { get; set; }
    public string? NormalizedContent { get; set; }     // normalized form; null when same as Content
    public decimal? Confidence { get; set; }
}

public sealed class MappingTemplate
{
    public int TemplateId { get; set; }
    public int DocumentTypeId { get; set; }
    public string Name { get; set; } = "";
    public string TargetModel { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public List<MappingField> Fields { get; set; } = new();
}

public sealed class MappingField
{
    public int FieldId { get; set; }
    public int TemplateId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";   // STRING/DECIMAL/DATE/INT/BOOL
    public bool IsRequired { get; set; }
    public string SourceType { get; set; } = "KEY_VALUE"; // KEY_VALUE/REGEX/TABLE_CELL/CONSTANT
    public string? KeyPattern { get; set; }
    public string? SourcePattern { get; set; }
    public string? TableHeader { get; set; }
    public string? RowSelector { get; set; }            // FIRST/LAST/ALL
    public string? DefaultValue { get; set; }
    public decimal MinConfidence { get; set; } = 0.60m;
}

/// <summary>Engine output container — what an OCR provider returns.</summary>
public sealed class OcrExtraction
{
    public string Engine { get; set; } = "";
    public string? EngineVersion { get; set; }
    public int PageCount { get; set; }
    public List<OcrTextBlock> TextBlocks { get; set; } = new();
    public List<OcrTable> Tables { get; set; } = new();
    public string? RawJson { get; set; }
}

public sealed class Processor
{
    public int ProcessorId { get; set; }
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "";
    public string ProcessorMode { get; set; } = "REALTIME";   // REALTIME / QUEUE
    public string? ConfigJson { get; set; }
    public bool StoreRawJson { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public sealed class DocumentProperty
{
    public long PropertyId { get; set; }
    public long DocumentId { get; set; }
    public long OcrRunId { get; set; }
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public decimal? Confidence { get; set; }
    public string? SourceRef { get; set; }
}
