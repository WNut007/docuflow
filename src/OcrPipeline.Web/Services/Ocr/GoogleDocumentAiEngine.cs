using System.Text;
using System.Text.Json;
using Google.Cloud.DocumentAI.V1;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Normalization;
// 'Document' is ambiguous between our Domain POCO and the Document AI proto; alias the proto.
using GcpDocument = Google.Cloud.DocumentAI.V1.Document;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Real Google Document AI engine. Calls the online ProcessDocument endpoint with a
/// Form Parser / Custom Doc Extractor processor and maps the returned Document proto
/// into our <see cref="OcrExtraction"/> (text blocks, KEY/VALUE pairs, tables).
///
/// Auth: uses Application Default Credentials. Set GOOGLE_APPLICATION_CREDENTIALS to a
/// service-account JSON key, or run `gcloud auth application-default login` in dev.
/// The service account needs role roles/documentai.apiUser.
///
/// NuGet: Google.Cloud.DocumentAI.V1
/// </summary>
public sealed class GoogleDocumentAiEngine(
    IOptions<GoogleDocAiOptions> options,
    TextNormalizer normalizer) : IOcrEngine
{
    private readonly GoogleDocAiOptions _o = options.Value;

    public string Name => "GOOGLE_DOCAI";

    public async Task<OcrExtraction> ExtractAsync(string filePath, string contentType, CancellationToken ct = default)
    {
        // region-specific endpoint (us-documentai.googleapis.com / eu-documentai.googleapis.com)
        var client = await new DocumentProcessorServiceClientBuilder
        {
            Endpoint = $"{_o.Location}-documentai.googleapis.com"
        }.BuildAsync(ct);

        // resource name: a pinned version is more reproducible than the default
        var name = string.IsNullOrWhiteSpace(_o.ProcessorVersion)
            ? $"projects/{_o.ProjectId}/locations/{_o.Location}/processors/{_o.ProcessorId}"
            : $"projects/{_o.ProjectId}/locations/{_o.Location}/processors/{_o.ProcessorId}/processorVersions/{_o.ProcessorVersion}";

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var request = new ProcessRequest
        {
            Name = name,
            RawDocument = new RawDocument
            {
                Content = ByteString.CopyFrom(bytes),
                MimeType = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType
            }
        };

        var response = await client.ProcessDocumentAsync(request, ct);

        // Online ProcessDocument has a per-request page limit; larger files need batch via GCS.
        if (response.Document.Pages.Count > _o.OnlinePageLimit)
            throw new NotSupportedException(
                $"Document has {response.Document.Pages.Count} pages, over the online limit of {_o.OnlinePageLimit}. " +
                "Batch processing via Google Cloud Storage is added in Prompt 7.");

        return Map(response.Document);
    }

    // ---- mapping: Document proto -> OcrExtraction -----------------------------
    private OcrExtraction Map(GcpDocument doc)
    {
        var ex = new OcrExtraction
        {
            Engine = Name,
            EngineVersion = _o.ProcessorVersion ?? "default",
            PageCount = doc.Pages.Count
        };

        var text = doc.Text ?? "";

        for (int pi = 0; pi < doc.Pages.Count; pi++)
        {
            var page = doc.Pages[pi];
            int pageNo = pi + 1;

            // KEY/VALUE pairs (form fields). Emit "Key: Value" so the mapping engine
            // and property derivation (which split on ':') stay consistent across engines.
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

        // normalize every block + cell (Thai digits / currency / Buddhist-era dates), storing raw + normalized
        var order = normalizer.InferDayMonthOrder(ex.TextBlocks.Select(b => b.Content));
        foreach (var b in ex.TextBlocks)
            b.NormalizedContent = normalizer.Normalize(b.Content, order).Normalized;
        foreach (var cell in ex.Tables.SelectMany(t => t.Cells))
            cell.NormalizedContent = normalizer.Normalize(cell.Content, order).Normalized;

        // optionally keep the raw proto as JSON for audit
        ex.RawJson = doc.ToString();
        return ex;
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
                // normalized cell geometry when the processor provides it (else null -> Tables-tab fallback)
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
