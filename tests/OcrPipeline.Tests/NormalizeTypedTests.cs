using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline coverage of <see cref="MappingEngine.NormalizeTyped"/> Thai-digit handling. The typed
/// (DATE/DECIMAL/INT) PARSE-FAILURE fallback must still Arabic-ize digits — export and review must
/// never see Thai numerals ๐-๙ — while the success path (clean Thai number / Buddhist-era date) is
/// unchanged. Pure: no Tesseract, no DB.
/// </summary>
public sealed class NormalizeTypedTests
{
    private static readonly MappingEngine Engine =
        new(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), new TextNormalizer());

    private static bool HasThaiDigit(string s) => s.Any(c => c >= '๐' && c <= '๙');

    [Theory]
    [InlineData("DECIMAL", "๑.๒.๓", "1.2.3")]              // two decimal points -> unparseable number
    [InlineData("INT", "๑.๒.๓", "1.2.3")]
    [InlineData("DATE", "๑๑/๐เท/๒๕๒๓", "11/0เท/2523")]      // garbled middle -> no dd/MM/yyyy match
    public void Typed_parse_failure_falls_back_to_arabic_digits(string dataType, string raw, string expected)
    {
        var result = Engine.NormalizeTyped(dataType, raw, DayMonthOrder.Unknown);
        var s = Assert.IsType<string>(result);
        Assert.Equal(expected, s);
        Assert.False(HasThaiDigit(s), "fallback must Arabic-ize Thai digits, never preserve ๐-๙");
    }

    [Fact]
    public void Clean_thai_decimal_parses_to_invariant_decimal()
    {
        // ๑,๒๓๔.๕๖ -> 1,234.56 -> 1234.56  (success path untouched)
        var result = Engine.NormalizeTyped("DECIMAL", "๑,๒๓๔.๕๖", DayMonthOrder.Unknown);
        Assert.Equal(1234.56m, Assert.IsType<decimal>(result));
    }

    [Fact]
    public void Clean_thai_buddhist_date_normalizes_to_iso_gregorian()
    {
        // ๑๑/๐๒/๒๕๖๒ -> 11/02/2562 BE -> 2019-02-11 (day-first default; พ.ศ. via year>=2400 -> -543)
        var result = Engine.NormalizeTyped("DATE", "๑๑/๐๒/๒๕๖๒", DayMonthOrder.Unknown);
        Assert.Equal("2019-02-11", Assert.IsType<string>(result));
    }
}
