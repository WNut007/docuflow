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
        string? whitelist = (hint ?? "TEXT").Trim().ToUpperInvariant() switch
        {
            "NUMERIC" => "0123456789.,-",
            "DATE"    => "0123456789/.-",
            "INT"     => "0123456789",
            _         => null // TEXT / unknown -> no restriction
        };
        int psm = psmOverride is { } p && p > 0 ? p : SingleLinePsm;
        return (psm, whitelist);
    }
}
