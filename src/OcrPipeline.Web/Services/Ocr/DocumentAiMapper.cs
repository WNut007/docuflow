using System.Text;
using Google.Cloud.DocumentAI.V1;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Normalization;
using GcpDocument = Google.Cloud.DocumentAI.V1.Document;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Maps a Google Document AI <c>Document</c> proto into our <see cref="OcrExtraction"/>. Shared by
/// the online (ProcessDocument) and batch (BatchProcessDocuments) paths so the mapping is identical
/// — KEY/VALUE pairs emitted as "Key: Value" blocks, plain lines, and tables as a cell grid, all
/// with normalized 0..1 bboxes. Normalization (Thai digits / currency / Buddhist-era dates) is the
/// shared <see cref="TextNormalizer"/>.
/// </summary>
public sealed class DocumentAiMapper(TextNormalizer normalizer)
{
    /// <summary>Map a single Document (online path) into a normalized extraction.</summary>
    public OcrExtraction Map(GcpDocument doc, string engine, string? version)
    {
        var ex = new OcrExtraction { Engine = engine, EngineVersion = version };
        ex.PageCount = MapInto(ex, doc, pageOffset: 0);
        Normalize(ex);
        ex.RawJson = doc.ToString();
        return ex;
    }

    /// <summary>
    /// Appends a Document's blocks/tables to <paramref name="ex"/>, offsetting page numbers by
    /// <paramref name="pageOffset"/> (so batch output shards merge into one document). Does NOT
    /// normalize — call <see cref="Normalize"/> once after all shards are appended. Returns the
    /// number of pages appended.
    /// </summary>
    public int MapInto(OcrExtraction ex, GcpDocument doc, int pageOffset)
    {
        var text = doc.Text ?? "";

        for (int pi = 0; pi < doc.Pages.Count; pi++)
        {
            var page = doc.Pages[pi];
            int pageNo = pageOffset + pi + 1;

            // KEY/VALUE pairs (form fields) emitted as "Key: Value" so mapping/property derivation stay consistent.
            foreach (var ff in page.FormFields)
            {
                var key = GetText(text, ff.FieldName?.TextAnchor).Replace(":", "").Trim();
                var val = GetText(text, ff.FieldValue?.TextAnchor).Trim();
                if (key.Length == 0) continue;

                var (l, t, w, h) = BBox(ff.FieldValue?.BoundingPoly);
                ex.TextBlocks.Add(new OcrTextBlock
                {
                    PageNumber = pageNo,
                    BlockType = "VALUE",
                    Content = $"{key}: {val}",
                    Confidence = (decimal?)ff.FieldValue?.Confidence,
                    BBoxLeft = l, BBoxTop = t, BBoxWidth = w, BBoxHeight = h
                });
            }

            // plain lines (useful for REGEX source fields)
            foreach (var line in page.Lines)
            {
                var content = GetText(text, line.Layout?.TextAnchor).Trim();
                if (content.Length == 0) continue;
                var (l, t, w, h) = BBox(line.Layout?.BoundingPoly);
                ex.TextBlocks.Add(new OcrTextBlock
                {
                    PageNumber = pageNo,
                    BlockType = "LINE",
                    Content = content,
                    Confidence = (decimal?)line.Layout?.Confidence,
                    BBoxLeft = l, BBoxTop = t, BBoxWidth = w, BBoxHeight = h
                });
            }

            // tables -> cell grid
            for (int ti = 0; ti < page.Tables.Count; ti++)
            {
                var srcTable = page.Tables[ti];
                var table = new OcrTable
                {
                    PageNumber = pageNo,
                    TableIndex = ti,
                    Confidence = (decimal?)srcTable.Layout?.Confidence,
                    RowCount = srcTable.HeaderRows.Count + srcTable.BodyRows.Count,
                    ColumnCount = srcTable.HeaderRows.FirstOrDefault()?.Cells.Count
                                  ?? srcTable.BodyRows.FirstOrDefault()?.Cells.Count ?? 0
                };

                int rowIndex = 0;
                AddRows(table, srcTable.HeaderRows, text, ref rowIndex, header: true);
                AddRows(table, srcTable.BodyRows, text, ref rowIndex, header: false);
                ex.Tables.Add(table);
            }
        }
        return doc.Pages.Count;
    }

    /// <summary>Applies the shared TextNormalizer to every block + cell (raw kept, normalized stored).</summary>
    public void Normalize(OcrExtraction ex)
    {
        var order = normalizer.InferDayMonthOrder(ex.TextBlocks.Select(b => b.Content));
        foreach (var b in ex.TextBlocks)
            b.NormalizedContent = normalizer.Normalize(b.Content, order).Normalized;
        foreach (var cell in ex.Tables.SelectMany(t => t.Cells))
            cell.NormalizedContent = normalizer.Normalize(cell.Content, order).Normalized;
    }

    private static void AddRows(
        OcrTable table,
        IEnumerable<GcpDocument.Types.Page.Types.Table.Types.TableRow> rows,
        string text, ref int rowIndex, bool header)
    {
        foreach (var row in rows)
        {
            int colIndex = 0;
            foreach (var cell in row.Cells)
            {
                var (l, t, w, h) = BBox(cell.Layout?.BoundingPoly);
                table.Cells.Add(new OcrTableCell
                {
                    RowIndex = rowIndex,
                    ColIndex = colIndex,
                    RowSpan = Math.Max(1, cell.RowSpan),
                    ColSpan = Math.Max(1, cell.ColSpan),
                    IsHeader = header,
                    Content = GetText(text, cell.Layout?.TextAnchor).Trim(),
                    Confidence = (decimal?)cell.Layout?.Confidence,
                    BBoxLeft = l, BBoxTop = t, BBoxWidth = w, BBoxHeight = h
                });
                colIndex += Math.Max(1, cell.ColSpan);
            }
            rowIndex++;
        }
    }

    /// <summary>Resolves text from a TextAnchor's segments against the full document text.</summary>
    private static string GetText(string fullText, GcpDocument.Types.TextAnchor? anchor)
    {
        if (anchor is null || anchor.TextSegments.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var seg in anchor.TextSegments)
        {
            int start = (int)seg.StartIndex;
            int end = (int)seg.EndIndex;
            if (end > start && end <= fullText.Length)
                sb.Append(fullText, start, end - start);
        }
        return sb.ToString();
    }

    /// <summary>Computes a normalized (0..1) bounding box from a BoundingPoly.</summary>
    private static (decimal?, decimal?, decimal?, decimal?) BBox(BoundingPoly? poly)
    {
        var verts = poly?.NormalizedVertices;
        if (verts is null || verts.Count == 0) return (null, null, null, null);

        float minX = verts.Min(v => v.X), maxX = verts.Max(v => v.X);
        float minY = verts.Min(v => v.Y), maxY = verts.Max(v => v.Y);
        return ((decimal)minX, (decimal)minY, (decimal)(maxX - minX), (decimal)(maxY - minY));
    }
}
