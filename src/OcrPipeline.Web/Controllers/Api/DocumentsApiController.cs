using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models.Api;
using OcrPipeline.Web.Services.Imaging;

namespace OcrPipeline.Web.Controllers.Api;

/// <summary>
/// Read-only JSON/image API that feeds the point-and-click mapping UI (Prompt 4).
/// Cookie-authenticated like the rest of the app. Reuses OcrRepository; no SQL here.
/// </summary>
[ApiController]
[Authorize]
[Route("api/documents")]
public sealed class DocumentsApiController(
    IDocumentRepository documents,
    OcrRepository ocrRepo) : ControllerBase
{
    /// <summary>Returns the rendered PNG preview for a single page.</summary>
    [HttpGet("{id:long}/pages/{n:int}/image")]
    public IActionResult GetPageImage(long id, int n)
    {
        var doc = documents.GetById(id);
        if (doc is null) return NotFound();
        if (n < 1 || (doc.PageCount > 0 && n > doc.PageCount)) return NotFound();

        // path is derived from the DB-stored path, never from user input
        var path = PagePreviewRenderer.PreviewPath(doc.StoredPath, n);
        if (!System.IO.File.Exists(path)) return NotFound();

        return File(System.IO.File.OpenRead(path), "image/png");
    }

    /// <summary>Returns pages (px), text blocks (normalized 0..1 bbox) and tables for the document.</summary>
    [HttpGet("{id:long}/ocr")]
    public ActionResult<OcrDocumentDto> GetOcr(long id)
    {
        var doc = documents.GetById(id);
        if (doc is null) return NotFound();

        var pages = documents.GetPages(id)
            .Select(p => new OcrPageDto(p.PageNumber, p.WidthPx ?? 0, p.HeightPx ?? 0))
            .ToList();

        var ocr = ocrRepo.LoadLatest(id);
        var blocks = new List<OcrBlockDto>();
        var tables = new List<OcrTableDto>();

        if (ocr is not null)
        {
            foreach (var b in ocr.TextBlocks)
            {
                if (string.Equals(b.BlockType, "WORD", StringComparison.OrdinalIgnoreCase))
                    continue; // word-level boxes are too granular for click-to-map

                blocks.Add(new OcrBlockDto(
                    Id: $"block-{b.TextBlockId}",
                    Page: b.PageNumber,
                    Type: MapBlockType(b.BlockType),
                    Text: b.Content,
                    NormalizedValue: b.NormalizedContent,
                    Confidence: b.Confidence,
                    Bbox: ToBBox(b)));
            }

            foreach (var t in ocr.Tables)
            {
                var cells = t.Cells
                    .Select(c => new OcrCellDto(
                        c.RowIndex, c.ColIndex, c.RowSpan, c.ColSpan, c.IsHeader,
                        "TABLE_CELL", c.Content, c.NormalizedContent, c.Confidence))
                    .ToList();

                tables.Add(new OcrTableDto(
                    Id: $"table-{t.OcrTableId}",
                    Page: t.PageNumber,
                    RowCount: t.RowCount,
                    ColumnCount: t.ColumnCount,
                    Cells: cells));
            }
        }

        return new OcrDocumentDto(id, pages, blocks, tables);
    }

    // KEY/VALUE survive as-is; everything else (LINE/PARAGRAPH/…) is a generic LINE box.
    private static string MapBlockType(string blockType) => blockType.ToUpperInvariant() switch
    {
        "KEY" => "KEY",
        "VALUE" => "VALUE",
        _ => "LINE"
    };

    private static BBoxDto? ToBBox(OcrTextBlock b)
        => b is { BBoxLeft: { } l, BBoxTop: { } t, BBoxWidth: { } w, BBoxHeight: { } h }
            ? new BBoxDto(l, t, w, h)
            : null;
}
