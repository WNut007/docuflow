using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OcrPipeline.Web.Services.Normalization;

/// <summary>Which component comes first in a numeric date like "26/02/2019".</summary>
public enum DayMonthOrder { Unknown, DayFirst, MonthFirst }

/// <summary>What the normalizer recognised a raw value as.</summary>
public enum NormalizedKind { Text, Number, Date }

/// <summary>Raw OCR text paired with its normalized form (null when unchanged/unrecognised).</summary>
public sealed record NormalizedValue(string Raw, string? Normalized, NormalizedKind Kind);

/// <summary>
/// PURE, offline text normalization shared by every OCR engine. No I/O, no engine or
/// network dependency, so it is fully unit-testable on plain strings. Handles:
///   - Thai digits ๐-๙ (U+0E50..U+0E59) -> 0-9
///   - numbers/currency -> invariant decimal (thousands separators stripped)
///   - dates dd/MM/yyyy and Buddhist era (พ.ศ.) -> Gregorian, with per-document
///     day/month order inference (e.g. "26/02" proves day-first)
/// </summary>
public sealed class TextNormalizer
{
    private const char ThaiZero = '๐';   // ๐
    private const char ThaiNine = '๙';   // ๙

    // dd/MM/yyyy with /, - or . separators (year 2 or 4 digits)
    private static readonly Regex DatePattern =
        new(@"(\d{1,2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{2,4})", RegexOptions.Compiled);

    // Buddhist-era marker: พ.ศ. / พศ (with or without dots/space)
    private static readonly Regex BuddhistMarker =
        new(@"พ\.?\s?ศ\.?", RegexOptions.Compiled);

    /// <summary>Maps Thai digits to ASCII; leaves everything else untouched.</summary>
    public static string NormalizeThaiDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
            sb.Append(ch >= ThaiZero && ch <= ThaiNine ? (char)('0' + (ch - ThaiZero)) : ch);
        return sb.ToString();
    }

    /// <summary>Parses a number/currency string (Thai or ASCII digits) into an invariant decimal.</summary>
    public bool TryNormalizeNumber(string? input, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(input)) return false;
        // strip everything but digits, decimal point and sign (drops , thousands sep, ฿, spaces, currency)
        var cleaned = Regex.Replace(NormalizeThaiDigits(input), @"[^\d.\-]", "");
        if (cleaned.Length == 0) return false;
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Infers day/month order from a set of date-like strings in one document.
    /// A component &gt; 12 in the first slot proves day-first; in the second slot, month-first.
    /// </summary>
    public DayMonthOrder InferDayMonthOrder(IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var m = DatePattern.Match(NormalizeThaiDigits(c));
            if (!m.Success) continue;
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (a > 12) return DayMonthOrder.DayFirst;
            if (b > 12) return DayMonthOrder.MonthFirst;
        }
        return DayMonthOrder.Unknown;
    }

    /// <summary>True when the string contains a dd/MM/yyyy-shaped token.</summary>
    public static bool LooksLikeDate(string? input)
        => !string.IsNullOrWhiteSpace(input) && DatePattern.IsMatch(NormalizeThaiDigits(input));

    /// <summary>
    /// Normalizes a numeric date to <see cref="DateOnly"/>. Detects Buddhist era by the
    /// พ.ศ. marker or a year &gt;= 2400 and converts to Gregorian (-543). When the order is
    /// Unknown it defaults to day-first (Thai convention), but an unambiguous component
    /// (&gt; 12) always wins.
    /// </summary>
    public bool TryNormalizeDate(string? input, DayMonthOrder order, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = NormalizeThaiDigits(input);
        bool isBuddhist = BuddhistMarker.IsMatch(s);

        var m = DatePattern.Match(s);
        if (!m.Success) return false;

        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int y = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (y < 100) y += y >= 50 ? 1900 : 2000;   // 2-digit year heuristic

        // resolve order: an out-of-range component is decisive, else use the hint (default day-first)
        var effective = order;
        if (a > 12) effective = DayMonthOrder.DayFirst;
        else if (b > 12) effective = DayMonthOrder.MonthFirst;
        else if (effective == DayMonthOrder.Unknown) effective = DayMonthOrder.DayFirst;

        int day, month;
        if (effective == DayMonthOrder.MonthFirst) { month = a; day = b; }
        else { day = a; month = b; }

        if (isBuddhist || y >= 2400) y -= 543;   // พ.ศ. -> ค.ศ.

        if (month is < 1 or > 12 || day is < 1 or > 31) return false;
        try { date = new DateOnly(y, month, day); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>
    /// Best-effort normalization of a single raw value. Dates are tried before numbers
    /// (a date's separators would otherwise be stripped into a meaningless integer).
    /// Falls back to Thai-digit-normalized text. Pass the document's inferred order for dates.
    /// </summary>
    public NormalizedValue Normalize(string? raw, DayMonthOrder order = DayMonthOrder.Unknown)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new NormalizedValue(raw ?? "", null, NormalizedKind.Text);

        if (LooksLikeDate(raw) && TryNormalizeDate(raw, order, out var d))
            return new NormalizedValue(raw, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NormalizedKind.Date);

        if (TryNormalizeNumber(raw, out var n))
            return new NormalizedValue(raw, n.ToString(CultureInfo.InvariantCulture), NormalizedKind.Number);

        var digits = NormalizeThaiDigits(raw);
        return new NormalizedValue(raw, digits == raw ? null : digits, NormalizedKind.Text);
    }
}
