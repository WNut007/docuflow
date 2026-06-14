using System.Text.Json;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline accuracy tests (no Tesseract/Google/network/DB): feed a synthetic OcrExtraction per
/// sample and run the mapping engine, asserting CLAUDE.md ground truth.
///
/// English fixture models samples/east-repair-invoice.png. Thai fixture models
/// samples/thai-invoice.png (produced by scripts/generate-thai-sample.ps1; the image itself is NOT
/// read here — these tests assert the normalization path, not OCR).
/// </summary>
public sealed class InvoicePipelineTests
{
    private static MappingEngine NewEngine()
        => new(new TransformerPipeline(Array.Empty<IValueTransformer>()), new TextNormalizer());

    private static OcrTextBlock Kv(string key, string value) =>
        new() { PageNumber = 1, BlockType = "VALUE", Content = $"{key}: {value}", Confidence = 0.95m };

    private static void AddRow(OcrTable t, int row, bool header, params string[] cells)
    {
        for (int c = 0; c < cells.Length; c++)
            t.Cells.Add(new OcrTableCell { RowIndex = row, ColIndex = c, IsHeader = header, Content = cells[c], Confidence = 0.95m });
    }

    private static MappingField KeyField(int id, string prop, string dataType, string key) => new()
    {
        FieldId = id, TargetProperty = prop, DataType = dataType, SourceType = "KEY_VALUE",
        KeyPattern = $"^{key}$", MinConfidence = 0.50m
    };

    private static async Task<JsonElement> RunAsync(MappingTemplate tpl, OcrExtraction ex,
        Dictionary<int, List<MappingTableColumn>>? cols = null)
    {
        var outcome = await NewEngine().RunAsync(tpl, ex,
            new Dictionary<int, List<TransformerStep>>(), cols);
        return JsonDocument.Parse(outcome.MappedJson).RootElement;
    }

    // ---- English: East Repair ground truth -----------------------------------

    [Fact]
    public async Task EastRepair_maps_to_ground_truth()
    {
        var ex = new OcrExtraction { Engine = "TEST", PageCount = 1 };
        ex.TextBlocks.AddRange(new[]
        {
            Kv("Invoice No", "US-001"),
            Kv("Date", "11/02/2019"),      // dd/MM — day-first proven by the Due Date below
            Kv("Due Date", "26/02/2019"),  // 26 > 12 -> document is day-first
            Kv("Subtotal", "145.00"),
            Kv("Sales Tax", "9.06"),
            Kv("Total", "154.06"),
        });
        var table = new OcrTable { PageNumber = 1, TableIndex = 0, RowCount = 4, ColumnCount = 4 };
        AddRow(table, 0, true, "QTY", "DESCRIPTION", "UNIT PRICE", "AMOUNT");
        AddRow(table, 1, false, "1", "Front and rear brake cables", "100.00", "100.00");
        AddRow(table, 2, false, "2", "New set of pedal arms", "15.00", "30.00");
        AddRow(table, 3, false, "3", "Labor 3hrs", "5.00", "15.00");
        ex.Tables.Add(table);

        var lineItem = new MappingField
        {
            FieldId = 10, TargetProperty = "line_item", SourceType = "TABLE_CELL",
            DataType = "STRING", TableHeader = "DESCRIPTION", RowSelector = "ALL"
        };
        var tpl = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "InvoiceModel",
            Fields =
            [
                KeyField(1, "invoice_id", "STRING", "Invoice No"),
                KeyField(2, "invoice_date", "DATE", "Date"),
                KeyField(3, "subtotal", "DECIMAL", "Subtotal"),
                KeyField(4, "sales_tax", "DECIMAL", "Sales Tax"),
                KeyField(5, "total", "DECIMAL", "Total"),
                lineItem
            ]
        };
        var cols = new Dictionary<int, List<MappingTableColumn>>
        {
            [10] =
            [
                new() { ColumnId = 1, FieldId = 10, TargetSubProperty = "description", DataType = "STRING",  TableHeader = "DESCRIPTION", SortOrder = 0 },
                new() { ColumnId = 2, FieldId = 10, TargetSubProperty = "qty",         DataType = "INT",     TableHeader = "QTY",         SortOrder = 1 },
                new() { ColumnId = 3, FieldId = 10, TargetSubProperty = "unit_price",  DataType = "DECIMAL", TableHeader = "UNIT PRICE",  SortOrder = 2 },
                new() { ColumnId = 4, FieldId = 10, TargetSubProperty = "amount",      DataType = "DECIMAL", TableHeader = "AMOUNT",      SortOrder = 3 },
            ]
        };

        var json = await RunAsync(tpl, ex, cols);

        Assert.Equal("US-001", json.GetProperty("invoice_id").GetString());
        Assert.Equal("2019-02-11", json.GetProperty("invoice_date").GetString());  // dd/MM, not 2019-11-02
        Assert.Equal("145.00", json.GetProperty("subtotal").GetString());
        Assert.Equal("9.06", json.GetProperty("sales_tax").GetString());
        Assert.Equal("154.06", json.GetProperty("total").GetString());

        var items = json.GetProperty("line_item");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(3, items.GetArrayLength());
        AssertRow(items[0], "Front and rear brake cables", 1, 100.00m, 100.00m);
        AssertRow(items[1], "New set of pedal arms", 2, 15.00m, 30.00m);
        AssertRow(items[2], "Labor 3hrs", 3, 5.00m, 15.00m);
    }

    private static void AssertRow(JsonElement row, string desc, int qty, decimal unit, decimal amount)
    {
        Assert.Equal(JsonValueKind.String, row.GetProperty("description").ValueKind);
        Assert.Equal(desc, row.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Number, row.GetProperty("qty").ValueKind);
        Assert.Equal(qty, row.GetProperty("qty").GetInt32());
        Assert.Equal(JsonValueKind.Number, row.GetProperty("amount").ValueKind);
        Assert.Equal(unit, row.GetProperty("unit_price").GetDecimal());
        Assert.Equal(amount, row.GetProperty("amount").GetDecimal());
    }

    // ---- Thai: Thai digits + Buddhist-era date through the same pipeline -------

    [Fact]
    public async Task ThaiInvoice_normalizes_thai_digits_and_buddhist_date()
    {
        // Models samples/thai-invoice.png: Thai digits ๐-๙ and a พ.ศ. (Buddhist era) date.
        var ex = new OcrExtraction { Engine = "TEST", PageCount = 1 };
        ex.TextBlocks.AddRange(new[]
        {
            Kv("เลขที่", "INV-๒๕๖๒-๐๐๑"),     // Thai digits in an id
            Kv("วันที่", "๑๑/๐๒/๒๕๖๒"),       // 11/02/2562 พ.ศ. -> 2019-02-11
            Kv("ครบกำหนด", "๒๖/๐๒/๒๕๖๒"),    // 26 > 12 -> day-first
            Kv("รวมทั้งสิ้น", "๑,๓๒๐.๙๘"),     // Thai digits + thousands sep
        });

        var tpl = new MappingTemplate
        {
            TemplateId = 2, TargetModel = "InvoiceModel",
            Fields =
            [
                KeyField(1, "invoice_id", "STRING", "เลขที่"),
                KeyField(2, "invoice_date", "DATE", "วันที่"),
                KeyField(3, "total", "DECIMAL", "รวมทั้งสิ้น"),
            ]
        };

        var json = await RunAsync(tpl, ex);

        Assert.Equal("INV-2562-001", json.GetProperty("invoice_id").GetString());   // ๒๕๖๒-๐๐๑ -> 2562-001
        Assert.Equal("2019-02-11", json.GetProperty("invoice_date").GetString());   // พ.ศ. 2562 -> ค.ศ. 2019, dd/MM
        Assert.Equal("1320.98", json.GetProperty("total").GetString());             // ๑,๓๒๐.๙๘ -> 1320.98
    }
}
