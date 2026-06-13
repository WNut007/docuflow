using System.Text.Json;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// MOCKUP engine. Returns a deterministic sample extraction so the pipeline can be
/// exercised end-to-end without a real OCR backend.
///
/// To make it real:
///   - Tesseract: shell out to `tesseract` (text) + a layout model for tables, OR
///     use the Tesseract NuGet wrapper. Tesseract gives text/words well but NOT
///     reliable tables — pair it with a table-structure model.
///   - Google Document AI: call the Form Parser / Custom Doc Extractor processor;
///     map `entities` -> KEY/VALUE blocks and `tables` -> OcrTable/OcrTableCell.
///   - Azure Form Recognizer / AWS Textract: same mapping shape, different SDK.
/// All providers must populate normalized (0..1) bounding boxes and confidences.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    public string Name => "Tesseract";

    public Task<OcrExtraction> ExtractAsync(string filePath, string contentType, CancellationToken ct = default)
    {
        var ex = new OcrExtraction
        {
            Engine = Name,
            EngineVersion = "5.x",
            PageCount = 1
        };

        // --- Sample text blocks (KEY/VALUE pairs the mapping engine can resolve) ---
        ex.TextBlocks.AddRange(new[]
        {
            Kv("Invoice No", "INV-2026-0042", 1, 0.97m),
            Kv("Invoice Date", "2026-06-10",   1, 0.95m),
            Kv("Vendor", "Acme Supplies Co.",  1, 0.93m),
            Line("Grand Total: 12,840.00", 1, 0.96m),
            Line("VAT: 840.00", 1, 0.94m),
        });

        // --- Sample table (line items) ---
        var table = new OcrTable
        {
            PageNumber = 1, TableIndex = 0, RowCount = 3, ColumnCount = 3, Confidence = 0.91m
        };
        AddRow(table, 0, true,  "Description", "Qty", "Amount");
        AddRow(table, 1, false, "Widget A", "2", "5,000.00");
        AddRow(table, 2, false, "Widget B", "1", "7,000.00");
        ex.Tables.Add(table);

        ex.RawJson = JsonSerializer.Serialize(new { note = "mockup extraction", filePath });
        return Task.FromResult(ex);
    }

    // ---- helpers --------------------------------------------------------------
    private static OcrTextBlock Kv(string key, string value, int page, decimal conf)
        => new()
        {
            PageNumber = page,
            BlockType = "VALUE",
            Content = $"{key}: {value}",   // a real engine keeps KEY and VALUE separate
            Confidence = conf
        };

    private static OcrTextBlock Line(string text, int page, decimal conf)
        => new() { PageNumber = page, BlockType = "LINE", Content = text, Confidence = conf };

    private static void AddRow(OcrTable t, int row, bool header, params string[] cells)
    {
        for (int c = 0; c < cells.Length; c++)
            t.Cells.Add(new OcrTableCell
            {
                RowIndex = row, ColIndex = c, IsHeader = header,
                Content = cells[c], Confidence = 0.90m
            });
    }
}
