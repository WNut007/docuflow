using System.Text.Json;
using OcrPipeline.Web.Services.Export;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>The exported model must never carry internal reserved keys (e.g. the per-row _pg page
/// marker added for the multi-page review UI). Pure string-in/string-out.</summary>
public sealed class ExportStripTests
{
    [Fact]
    public void Removes_underscore_keys_from_nested_rows_keeps_real_data()
    {
        const string json = """{"invoice_id":"US-001","line_item":[{"qty":1,"_pg":1},{"qty":2,"_pg":3}]}""";
        using var doc = JsonDocument.Parse(ExportService.StripInternalKeys(json));

        Assert.Equal("US-001", doc.RootElement.GetProperty("invoice_id").GetString());
        var rows = doc.RootElement.GetProperty("line_item");
        Assert.False(rows[0].TryGetProperty("_pg", out _));   // provenance gone
        Assert.False(rows[1].TryGetProperty("_pg", out _));
        Assert.Equal(1, rows[0].GetProperty("qty").GetInt32());   // real cells preserved
        Assert.Equal(2, rows[1].GetProperty("qty").GetInt32());
    }

    [Fact]
    public void Returns_input_unchanged_on_parse_failure()
        => Assert.Equal("not json", ExportService.StripInternalKeys("not json"));
}
