namespace OcrPipeline.Web.Services.Mapping;

public enum ConfidenceBand { High, Medium, Low }

/// <summary>
/// THE single place that turns a confidence + threshold into a colour band. Used by the review
/// screen to colour each value/box (green high / amber medium / red low). Pure and unit-testable.
/// The cutoff is Ocr:MinPageConfidence (the medium/low boundary); the high/medium boundary is
/// derived as the midpoint between the cutoff and 1.0, so a single config value drives everything.
/// </summary>
public static class ConfidenceBands
{
    public static ConfidenceBand Band(decimal? confidence, decimal cutoff)
    {
        if (confidence is not { } c || c < cutoff) return ConfidenceBand.Low;     // unknown or below cutoff
        decimal high = cutoff + (1m - cutoff) / 2m;
        return c >= high ? ConfidenceBand.High : ConfidenceBand.Medium;
    }

    /// <summary>Bootstrap contextual class for a band (green / amber / red).</summary>
    public static string CssClass(ConfidenceBand band) => band switch
    {
        ConfidenceBand.High => "success",
        ConfidenceBand.Medium => "warning",
        _ => "danger"
    };
}
