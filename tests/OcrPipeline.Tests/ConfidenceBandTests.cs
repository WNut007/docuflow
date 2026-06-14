using OcrPipeline.Web.Services.Mapping;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Offline tests for the single confidence-band helper (cutoff = Ocr:MinPageConfidence).</summary>
public sealed class ConfidenceBandTests
{
    private const decimal Cutoff = 0.60m;   // high/medium boundary derives to 0.80

    [Theory]
    [InlineData(0.95, ConfidenceBand.High)]
    [InlineData(0.80, ConfidenceBand.High)]    // boundary is inclusive -> High
    [InlineData(0.79, ConfidenceBand.Medium)]
    [InlineData(0.60, ConfidenceBand.Medium)]  // exactly at cutoff -> Medium
    [InlineData(0.59, ConfidenceBand.Low)]
    [InlineData(0.0, ConfidenceBand.Low)]
    public void Band_classifies_by_thresholds(double confidence, ConfidenceBand expected)
        => Assert.Equal(expected, ConfidenceBands.Band((decimal)confidence, Cutoff));

    [Fact]
    public void Band_treats_null_confidence_as_low()
        => Assert.Equal(ConfidenceBand.Low, ConfidenceBands.Band(null, Cutoff));

    [Theory]
    [InlineData(ConfidenceBand.High, "success")]
    [InlineData(ConfidenceBand.Medium, "warning")]
    [InlineData(ConfidenceBand.Low, "danger")]
    public void CssClass_maps_band_to_bootstrap_colour(ConfidenceBand band, string expected)
        => Assert.Equal(expected, ConfidenceBands.CssClass(band));
}
