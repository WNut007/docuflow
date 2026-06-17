using System.Text.Json;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline coverage of the review-screen line_item round-trip (no DB/OCR): stored typed JSON ->
/// display strings (scale preserved) -> edited strings -> typed JSON again via the SAME
/// MappingEngine.NormalizeTyped path extraction uses. Locks that qty stays int, prices stay decimals
/// with scale, and edits survive.
/// </summary>
public sealed class LineItemTableTests
{
    private static MappingTableColumn Col(string sub, string dt, int sort)
        => new() { TargetSubProperty = sub, DataType = dt, SortOrder = sort, IsActive = true };

    private static readonly List<MappingTableColumn> Cols = new()
    {
        Col("qty", "INT", 0), Col("description", "STRING", 1),
        Col("unit_price", "DECIMAL", 2), Col("amount", "DECIMAL", 3),
    };

    private static Func<string, string?, object?> Typer()
    {
        var engine = new MappingEngine(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), new TextNormalizer());
        return (dt, raw) => engine.NormalizeTyped(dt, raw, DayMonthOrder.Unknown);
    }

    [Fact]
    public void Parse_preserves_decimal_scale_ints_strings_and_missing_cells()
    {
        const string json = """[{"qty":1,"description":"Brake cables","unit_price":100.00,"amount":100.00},{"qty":2,"description":"Pedals"}]""";
        var rows = LineItemTable.Parse(json, Cols);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["qty"]);
        Assert.Equal("Brake cables", rows[0]["description"]);
        Assert.Equal("100.00", rows[0]["unit_price"]);   // raw JSON text -> scale kept
        Assert.Equal("100.00", rows[0]["amount"]);
        Assert.Equal("", rows[1]["unit_price"]);          // missing -> ""
        Assert.Equal("", rows[1]["amount"]);
    }

    [Fact]
    public void Parse_of_null_or_blank_yields_no_rows()
    {
        Assert.Empty(LineItemTable.Parse(null, Cols));
        Assert.Empty(LineItemTable.Parse("   ", Cols));
    }

    [Fact]
    public void BuildTypedRows_emits_int_qty_and_decimal_prices_with_scale()
    {
        var rows = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["qty"] = "1", ["description"] = "Brake cables", ["unit_price"] = "100.00", ["amount"] = "100.00" },
        };

        var typed = LineItemTable.BuildTypedRows(Cols, rows, Typer());
        var json = JsonSerializer.Serialize(typed);

        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement[0];
        Assert.Equal(JsonValueKind.Number, row.GetProperty("qty").ValueKind);
        Assert.Equal(1L, row.GetProperty("qty").GetInt64());
        Assert.Equal(JsonValueKind.String, row.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Number, row.GetProperty("amount").ValueKind);
        Assert.Equal("100.00", row.GetProperty("amount").GetRawText());     // decimal scale survives the round-trip
        Assert.Equal("100.00", row.GetProperty("unit_price").GetRawText());
    }

    [Fact]
    public void Edited_cell_is_reflected_in_the_rebuilt_json()
    {
        // start from stored JSON, parse, edit qty 1 -> 2, rebuild
        var rows = LineItemTable.Parse("""[{"qty":1,"description":"Brake","unit_price":100.00,"amount":100.00}]""", Cols)
                   .Cast<IReadOnlyDictionary<string, string>>().ToList();
        ((Dictionary<string, string>)rows[0])["qty"] = "2";

        var typed = LineItemTable.BuildTypedRows(Cols, rows, Typer());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(typed));
        Assert.Equal(2L, doc.RootElement[0].GetProperty("qty").GetInt64());
    }

    [Fact]
    public void ReadPageTags_reads_pg_or_defaults_to_one()
    {
        const string json = """[{"qty":1,"_pg":1},{"qty":2,"_pg":3},{"qty":3}]""";
        Assert.Equal(new[] { 1, 3, 1 }, LineItemTable.ReadPageTags(json).ToArray());   // missing -> 1
        Assert.Empty(LineItemTable.ReadPageTags(null));
    }

    [Fact]
    public void BuildTypedRows_preserves_page_marker_through_round_trip()
    {
        var rows = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["qty"] = "1", ["description"] = "a", ["_pg"] = "2" },
        };
        var typed = LineItemTable.BuildTypedRows(Cols, rows, Typer());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(typed));
        Assert.Equal(2, doc.RootElement[0].GetProperty("_pg").GetInt32());                 // provenance survives
        Assert.Equal(JsonValueKind.Number, doc.RootElement[0].GetProperty("qty").ValueKind); // cells still typed
    }

    [Fact]
    public void A_string_typed_amount_column_emits_a_quoted_string()
    {
        // matches current graceful extraction behavior: a STRING column keeps a number-looking value as text
        var cols = new List<MappingTableColumn> { Col("qty", "INT", 0), Col("amount", "STRING", 1) };
        var rows = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["qty"] = "3", ["amount"] = "15.00" },
        };

        var typed = LineItemTable.BuildTypedRows(cols, rows, Typer());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(typed));
        Assert.Equal(JsonValueKind.String, doc.RootElement[0].GetProperty("amount").ValueKind);
        Assert.Equal("15.00", doc.RootElement[0].GetProperty("amount").GetString());
    }
}
