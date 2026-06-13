using System.Text.RegularExpressions;

namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// Pure helpers that turn a clicked OCR block into a mapping binding, WITHOUT ever exposing
/// regex/patterns to the user. The point-and-click UI sends the block type + text; the server
/// derives SourceType and (for KEY_VALUE) a KeyPattern. Kept pure so it is unit-testable offline.
/// </summary>
public static class BindingInference
{
    /// <summary>Maps an OCR block type to a mapping SourceType.</summary>
    public static string InferSourceType(string? blockType) => (blockType ?? "").ToUpperInvariant() switch
    {
        "TABLE_CELL" => "TABLE_CELL",
        "KEY" => "KEY_VALUE",
        "VALUE" => "KEY_VALUE",
        _ => "KEY_VALUE" // LINE/PARAGRAPH etc. bind by their "Key: Value" text
    };

    /// <summary>Extracts the key portion of a "Key: Value" block (text before the first colon).</summary>
    public static string KeyFromBlockText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        int idx = content.IndexOf(':');
        return (idx > 0 ? content[..idx] : content).Trim();
    }

    /// <summary>
    /// Builds an anchored, escaped KeyPattern for a KEY_VALUE binding. Stored server-side only;
    /// the user never sees or edits it. Matches the key exactly (case-insensitive via the engine).
    /// </summary>
    public static string KeyPatternFor(string key) => $"^{Regex.Escape(key.Trim())}$";
}
