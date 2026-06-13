namespace OcrPipeline.Web.Models.Api;

/// <summary>Normalized (0..1) bounding box for an OCR block.</summary>
public sealed record BBoxDto(decimal Left, decimal Top, decimal Width, decimal Height);

/// <summary>A page's pixel dimensions (matches the rendered PNG preview).</summary>
public sealed record OcrPageDto(int PageNumber, int Width, int Height);

/// <summary>A clickable OCR text block (KEY/VALUE/LINE) with raw + normalized text.</summary>
public sealed record OcrBlockDto(
    string Id, int Page, string Type, string Text, string? NormalizedValue,
    decimal? Confidence, BBoxDto? Bbox);

/// <summary>One table cell; Type is always "TABLE_CELL".</summary>
public sealed record OcrCellDto(
    int RowIndex, int ColIndex, int RowSpan, int ColSpan, bool IsHeader,
    string Type, string? Text, string? NormalizedValue, decimal? Confidence);

/// <summary>A detected table and its cell grid.</summary>
public sealed record OcrTableDto(
    string Id, int Page, int RowCount, int ColumnCount, IReadOnlyList<OcrCellDto> Cells);

/// <summary>Full OCR payload the mapping UI consumes for one document.</summary>
public sealed record OcrDocumentDto(
    long DocumentId,
    IReadOnlyList<OcrPageDto> Pages,
    IReadOnlyList<OcrBlockDto> Blocks,
    IReadOnlyList<OcrTableDto> Tables);
