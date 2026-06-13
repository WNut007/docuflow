using System.Text.RegularExpressions;
using OcrPipeline.Web.Services.Mapping;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests for the click-to-bind inference: block type -> SourceType, and deriving a
/// server-side KeyPattern from a "Key: Value" block (the user never sees the regex).
/// </summary>
public sealed class BindingInferenceTests
{
    [Theory]
    [InlineData("KEY", "KEY_VALUE")]
    [InlineData("VALUE", "KEY_VALUE")]
    [InlineData("value", "KEY_VALUE")]   // case-insensitive
    [InlineData("TABLE_CELL", "TABLE_CELL")]
    [InlineData("LINE", "KEY_VALUE")]    // fallback binds by text
    public void InferSourceType_maps_block_type(string blockType, string expected)
        => Assert.Equal(expected, BindingInference.InferSourceType(blockType));

    [Theory]
    [InlineData("Invoice No: INV-001", "Invoice No")]
    [InlineData("Due Date : 26/02/2019", "Due Date")]
    [InlineData("Subtotal", "Subtotal")]   // no colon -> whole text
    public void KeyFromBlockText_takes_text_before_colon(string content, string expected)
        => Assert.Equal(expected, BindingInference.KeyFromBlockText(content));

    [Fact]
    public void KeyPatternFor_is_anchored_escaped_and_matches_only_the_key()
    {
        var pattern = BindingInference.KeyPatternFor("Invoice No.");
        Assert.StartsWith("^", pattern);
        Assert.EndsWith("$", pattern);

        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        Assert.Matches(rx, "Invoice No.");
        Assert.DoesNotMatch(rx, "Invoice Number");   // '.' is escaped, not a wildcard
        Assert.DoesNotMatch(rx, "Other Invoice No.");// anchored
    }
}
