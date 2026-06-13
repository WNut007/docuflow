using OcrPipeline.Web.Services.Normalization;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Pure, offline unit tests for <see cref="TextNormalizer"/> — no Tesseract or Google
/// dependency, so they run anywhere without native binaries, tessdata or network.
/// </summary>
public sealed class NormalizationTests
{
    private readonly TextNormalizer _n = new();

    // ---- Thai digits ----------------------------------------------------------

    [Theory]
    [InlineData("๑๒๓", "123")]
    [InlineData("๐๙", "09")]
    [InlineData("INV-๒๐๒๖", "INV-2026")]
    [InlineData("abc", "abc")]            // untouched
    public void NormalizeThaiDigits_maps_thai_to_ascii(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.NormalizeThaiDigits(input));

    // ---- numbers / currency ---------------------------------------------------

    [Theory]
    [InlineData("145.00", 145.00)]
    [InlineData("12,840.00", 12840.00)]
    [InlineData("฿ ๒,๕๐๐.๗๕", 2500.75)]   // Thai digits + currency + thousands sep
    [InlineData("9.06", 9.06)]
    public void TryNormalizeNumber_parses_to_decimal(string input, double expected)
    {
        Assert.True(_n.TryNormalizeNumber(input, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void TryNormalizeNumber_rejects_non_numbers(string input)
        => Assert.False(_n.TryNormalizeNumber(input, out _));

    // ---- Buddhist era -> Gregorian -------------------------------------------

    [Fact]
    public void TryNormalizeDate_converts_buddhist_thai_digits_to_gregorian()
    {
        // ๒๖/๐๒/๒๕๖๒ = 26/02/2562 พ.ศ. -> 2019-02-26
        Assert.True(_n.TryNormalizeDate("๒๖/๐๒/๒๕๖๒", DayMonthOrder.DayFirst, out var date));
        Assert.Equal(new DateOnly(2019, 2, 26), date);
    }

    [Fact]
    public void TryNormalizeDate_detects_buddhist_era_by_year_threshold()
    {
        Assert.True(_n.TryNormalizeDate("11/02/2562", DayMonthOrder.DayFirst, out var date));
        Assert.Equal(new DateOnly(2019, 2, 11), date);
    }

    [Fact]
    public void TryNormalizeDate_keeps_gregorian_year_unchanged()
    {
        Assert.True(_n.TryNormalizeDate("26/02/2019", DayMonthOrder.DayFirst, out var date));
        Assert.Equal(new DateOnly(2019, 2, 26), date);
    }

    // ---- day/month order inference -------------------------------------------

    [Fact]
    public void InferDayMonthOrder_detects_day_first_from_26_over_12()
    {
        var order = _n.InferDayMonthOrder(new[] { "11/02/2019", "26/02/2019" });
        Assert.Equal(DayMonthOrder.DayFirst, order);
    }

    [Fact]
    public void InferDayMonthOrder_detects_month_first_when_second_component_exceeds_12()
    {
        var order = _n.InferDayMonthOrder(new[] { "02/26/2019" });
        Assert.Equal(DayMonthOrder.MonthFirst, order);
    }

    [Fact]
    public void InferDayMonthOrder_returns_unknown_when_ambiguous()
    {
        var order = _n.InferDayMonthOrder(new[] { "01/02/2019", "03/04/2019" });
        Assert.Equal(DayMonthOrder.Unknown, order);
    }

    [Fact]
    public void TryNormalizeDate_unambiguous_component_overrides_month_first_hint()
    {
        // 26 can't be a month, so day-first wins even with a MonthFirst hint
        Assert.True(_n.TryNormalizeDate("26/02/2019", DayMonthOrder.MonthFirst, out var date));
        Assert.Equal(new DateOnly(2019, 2, 26), date);
    }

    // ---- end-to-end dispatcher ------------------------------------------------

    [Fact]
    public void Normalize_classifies_date_before_number()
    {
        var nv = _n.Normalize("26/02/2019", DayMonthOrder.DayFirst);
        Assert.Equal(NormalizedKind.Date, nv.Kind);
        Assert.Equal("2019-02-26", nv.Normalized);
    }

    [Fact]
    public void Normalize_classifies_currency_as_number()
    {
        var nv = _n.Normalize("฿12,840.00");
        Assert.Equal(NormalizedKind.Number, nv.Kind);
        Assert.Equal("12840.00", nv.Normalized);
    }
}
