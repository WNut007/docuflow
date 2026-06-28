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
    /// <summary>Template the document is processed with; null = let the pipeline resolve by page count.</summary>
    public int? TemplateId { get; set; }
    public decimal? ClassifyConfidence { get; set; }
    public string StatusCode { get; set; } = "CAPTURED";
    public int PageCount { get; set; }
    public int? UploadedByUserId { get; set; }
    /// <summary>Optional per-document Tesseract language override (e.g. "eng" or "tha+eng"); null = configured default.</summary>
    public string? OcrLanguages { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class DocumentPage
{
    public long PageId { get; set; }
    public long DocumentId { get; set; }
    public int PageNumber { get; set; }
    public int? WidthPx { get; set; }
    public int? HeightPx { get; set; }
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
    // normalized (0..1) bounding box so a cell can be clicked on the page image; null when the
    // engine has no cell geometry (e.g. Tesseract, or a processor returning only pixel vertices)
    public decimal? BBoxLeft { get; set; }
    public decimal? BBoxTop { get; set; }
    public decimal? BBoxWidth { get; set; }
    public decimal? BBoxHeight { get; set; }
}

public sealed class MappingTemplate
{
    public int TemplateId { get; set; }
    public int DocumentTypeId { get; set; }
    public string Name { get; set; } = "";
    public string TargetModel { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    /// <summary>OCR_FIRST (default, block-based mapping) or ZONAL (OCR only inside drawn zones).</summary>
    public string MappingMode { get; set; } = "OCR_FIRST";
    /// <summary>The sample document this template's zones are drawn over (the designer backdrop).
    /// NULL = no sample yet (designer shows an empty-state). FK -> Document(DocumentId).</summary>
    public long? SampleDocumentId { get; set; }
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

    // Zonal mapping (Phase 0): a normalized 0..1 rectangle drawn on a sample document, plus an OCR
    // hint and optional PageSegMode override. ZoneX is null when the field has no zone.
    public int? ZonePage { get; set; }
    public decimal? ZoneX { get; set; }
    public decimal? ZoneY { get; set; }
    public decimal? ZoneW { get; set; }
    public decimal? ZoneH { get; set; }
    public string? ZoneOcrHint { get; set; }            // TEXT/NUMERIC/DATE/INT
    public byte? ZonePsm { get; set; }
    /// <summary>Multi-page role this zone applies to (Phase 3): FIRST/CONTINUATION/LAST/ANY; null = ANY.</summary>
    public string? ZonePageRole { get; set; }
}

/// <summary>
/// A sub-column of a TABLE_CELL mapping field (e.g. line_item -> description/qty/unit_price/amount).
/// Each maps an OCR table column (matched by TableHeader) to a sub-property with its own DataType.
/// </summary>
public sealed class MappingTableColumn
{
    public int ColumnId { get; set; }
    public int FieldId { get; set; }
    public string TargetSubProperty { get; set; } = "";
    public string DataType { get; set; } = "STRING";   // STRING/DECIMAL/DATE/INT/BOOL
    public string? TableHeader { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    // Table-zone columns (Phase 2; dormant until then). Column x-boundaries within the table zone,
    // the row-bounding anchor flag, and how a multi-line cell collapses to a single value.
    public decimal? ColXStart { get; set; }
    public decimal? ColXEnd { get; set; }
    public bool IsAnchor { get; set; }
    public string? LineSelectMode { get; set; }         // ALL/PICK/FIRST/ANCHOR
    public string? LineSelectIndices { get; set; }      // e.g. "0,2"
    public string? LineJoinSeparator { get; set; }      // e.g. " "

    // ANCHOR mode only: signed line offset from the anchor (quantity) line. null/0 = the anchor line
    // itself (part-1 behavior). -N = the Nth distinct line ABOVE the anchor, +N = Nth line BELOW.
    // Reads only its side; the other side / past-end / crossing the adjacent anchor / a follower row all
    // return EMPTY (never a wrong-but-valid value). For metadata at a non-constant per-page offset
    // (e.g. our_reference on a split layout) use label-anchored extraction instead.
    public int? LineOffset { get; set; }
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

public sealed class ExportTarget
{
    public int TargetId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "REST_WEBHOOK";   // REST_WEBHOOK / ERP
    public string? Endpoint { get; set; }
    public string? AuthHeaderName { get; set; }
    public string? AuthSecret { get; set; }              // HMAC key / auth value — never logged
    public int? DocumentTypeId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ExportLog
{
    public long LogId { get; set; }
    public long DocumentId { get; set; }
    public int? TargetId { get; set; }
    public string StatusCode { get; set; } = "";         // SUCCESS / FAILED
    public int? HttpStatus { get; set; }
    public string? ResponseSnippet { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public string? TargetName { get; set; }              // joined for the admin UI (not a column)
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
