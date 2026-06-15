namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Pure mapping from a field's zone OCR hint to Tesseract settings: a PageSegMode and an optional
/// character whitelist. A tight, single-line PSM plus a content-specific whitelist is the lever that
/// makes a cropped zone read cleanly (no layout analysis, no stray glyphs from an unused script).
/// </summary>
public static class ZoneHint
{
    public const int SingleLinePsm = 7; // Tesseract PageSegMode.SingleLine

    public static (int Psm, string? Whitelist) Resolve(string? hint, byte? psmOverride)
    {
        // Whitelists ADD Thai digits ๐-๙ (U+0E50–0E59) alongside Arabic so Tesseract can emit Thai
        // numerals on a Thai zone (TextNormalizer Arabic-izes them downstream). English reads are
        // unaffected — 0-9 . , - / are all still present; we only widen the allowed set.
        string? whitelist = (hint ?? "TEXT").Trim().ToUpperInvariant() switch
        {
            "NUMERIC" => "0123456789๐๑๒๓๔๕๖๗๘๙.,-",
            "DATE"    => "0123456789๐๑๒๓๔๕๖๗๘๙/.-",
            "INT"     => "0123456789๐๑๒๓๔๕๖๗๘๙",
            _         => null // TEXT / unknown -> no restriction
        };
        int psm = psmOverride is { } p && p > 0 ? p : SingleLinePsm;
        return (psm, whitelist);
    }
}
