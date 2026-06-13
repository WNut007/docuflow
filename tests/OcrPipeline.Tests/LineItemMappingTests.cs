using System.Text.Json;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline ground-truth test (no DB, no OCR): a synthetic East Repair invoice table is mapped
/// through a multi-column TABLE_CELL field and must produce the 3 typed line_item objects from
/// CLAUDE.md. Asserts TYPED JSON (numbers for qty/unit_price/amount, string for description).
/// </summary>
public sealed class LineItemMappingTests
{
    private static MappingEngine NewEngine()
        => new(new TransformerPipeline(Array.Empty<IValueTransformer>()), new TextNormalizer());

    // Builds the line-items table exactly as an OCR engine would hand it over.
    private static OcrExtraction SampleExtraction()
    {
        var table = new OcrTable { PageNumber = 1, TableIndex = 0, RowCount = 4, ColumnCount = 4 };
        AddRow(table, 0, header: true, "Description", "Qty", "Unit Price", "Amount");
        AddRow(table, 1, header: false, "Front and rear brake cables", "1", "100.00", "100.00");
        AddRow(table, 2, header: false, "New set of pedal arms", "2", "15.00", "30.00");
        AddRow(table, 3, header: false, "Labor 3hrs", "3", "5.00", "15.00");

        var ex = new OcrExtraction { Engine = "TEST", PageCount = 1 };
        ex.Tables.Add(table);
        return ex;
    }

    private static void AddRow(OcrTable t, int row, bool header, params string[] cells)
    {
        for (int c = 0; c < cells.Length; c++)
            t.Cells.Add(new OcrTableCell
            {
                RowIndex = row, ColIndex = c, IsHeader = header, Content = cells[c], Confidence = 0.95m
            });
    }

    private static (MappingTemplate tpl, Dictionary<int, List<MappingTableColumn>> cols) BuildTemplate()
    {
        var field = new MappingField
        {
            FieldId = 1, TargetProperty = "line_item", SourceType = "TABLE_CELL",
            DataType = "STRING", TableHeader = "Description", RowSelector = "ALL"
        };
        var tpl = new MappingTemplate { TemplateId = 1, TargetModel = "InvoiceModel", Fields = [field] };

        var cols = new Dictionary<int, List<MappingTableColumn>>
        {
            [1] =
            [
                new() { ColumnId = 1, FieldId = 1, TargetSubProperty = "description", DataType = "STRING",  TableHeader = "Description", SortOrder = 0 },
                new() { ColumnId = 2, FieldId = 1, TargetSubProperty = "qty",         DataType = "INT",     TableHeader = "Qty",         SortOrder = 1 },
                new() { ColumnId = 3, FieldId = 1, TargetSubProperty = "unit_price",  DataType = "DECIMAL", TableHeader = "Unit Price",  SortOrder = 2 },
                new() { ColumnId = 4, FieldId = 1, TargetSubProperty = "amount",      DataType = "DECIMAL", TableHeader = "Amount",      SortOrder = 3 },
            ]
        };
        return (tpl, cols);
    }

    [Fact]
    public async Task LineItem_maps_to_three_typed_objects_matching_ground_truth()
    {
        var (tpl, cols) = BuildTemplate();
        var outcome = await NewEngine().RunAsync(
            tpl, SampleExtraction(),
            new Dictionary<int, List<TransformerStep>>(), cols);

        using var doc = JsonDocument.Parse(outcome.MappedJson);
        var items = doc.RootElement.GetProperty("line_item");

        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(3, items.GetArrayLength());

        AssertRow(items[0], "Front and rear brake cables", 1, 100.00m, 100.00m);
        AssertRow(items[1], "New set of pedal arms",       2,  15.00m,  30.00m);
        AssertRow(items[2], "Labor 3hrs",                  3,   5.00m,  15.00m);
    }

    private static void AssertRow(JsonElement row, string desc, int qty, decimal unit, decimal amount)
    {
        // description is a JSON string
        Assert.Equal(JsonValueKind.String, row.GetProperty("description").ValueKind);
        Assert.Equal(desc, row.GetProperty("description").GetString());

        // qty / unit_price / amount are JSON numbers (typed)
        Assert.Equal(JsonValueKind.Number, row.GetProperty("qty").ValueKind);
        Assert.Equal(qty, row.GetProperty("qty").GetInt32());

        Assert.Equal(JsonValueKind.Number, row.GetProperty("unit_price").ValueKind);
        Assert.Equal(unit, row.GetProperty("unit_price").GetDecimal());

        Assert.Equal(JsonValueKind.Number, row.GetProperty("amount").ValueKind);
        Assert.Equal(amount, row.GetProperty("amount").GetDecimal());
    }
}
