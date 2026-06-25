namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Gate for the lexicon-based <see cref="LineItemConsolidator"/>. The footer/subtotal drop keywords and
/// the description metadata vocabulary in <see cref="ConsolidateOptions"/> are LAYOUT-SPECIFIC (currently
/// the Michelin / B.F. Goodrich shipping invoice), so they must NOT run on every template: words like
/// "Origin", "Your Order", or "FACTORY" are common logistics terms another invoice could legitimately
/// carry in a real line item, and a blanket heuristic would wrongly strip/drop them. Consolidation runs
/// ONLY for templates whose id is listed here; an empty list (the default) means no template gets it.
/// Bound from config "Ocr:LineItemConsolidation".
/// </summary>
public sealed class LineItemConsolidationOptions
{
    /// <summary>Template ids the Michelin consolidation lexicon applies to. Empty (default) = off for all.</summary>
    public int[] TemplateIds { get; set; } = Array.Empty<int>();

    /// <summary>True when <paramref name="templateId"/> is gated in for consolidation.</summary>
    public bool AppliesTo(int templateId) => Array.IndexOf(TemplateIds, templateId) >= 0;
}
