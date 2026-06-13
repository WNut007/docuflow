using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OcrPipeline.Web.Services.Transform;

// ---- helpers ---------------------------------------------------------------
internal static class ConfigExtensions
{
    public static string? Str(this JsonElement cfg, string name)
        => cfg.ValueKind == JsonValueKind.Object &&
           cfg.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    public static int? Int(this JsonElement cfg, string name)
        => cfg.ValueKind == JsonValueKind.Object &&
           cfg.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;
}

/// <summary>trim — removes leading/trailing whitespace.</summary>
public sealed class TrimTransformer : IValueTransformer
{
    public string Type => "trim";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
        => Task.FromResult(value?.Trim());
}

/// <summary>case — config: { "mode": "upper" | "lower" | "title" }.</summary>
public sealed class CaseTransformer : IValueTransformer
{
    public string Type => "case";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
    {
        if (value is null) return Task.FromResult<string?>(null);
        var result = config.Str("mode") switch
        {
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),
            _ => value
        };
        return Task.FromResult<string?>(result);
    }
}

/// <summary>regex_replace — config: { "pattern": "...", "replacement": "..." }.</summary>
public sealed class RegexReplaceTransformer : IValueTransformer
{
    public string Type => "regex_replace";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
    {
        if (value is null) return Task.FromResult<string?>(null);
        var pattern = config.Str("pattern");
        if (pattern is null) return Task.FromResult<string?>(value);
        var replacement = config.Str("replacement") ?? "";
        return Task.FromResult<string?>(Regex.Replace(value, pattern, replacement));
    }
}

/// <summary>number_clean — strips thousands separators, formats to N decimals.</summary>
public sealed class NumberCleanTransformer : IValueTransformer
{
    public string Type => "number_clean";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return Task.FromResult<string?>(value);
        var cleaned = Regex.Replace(value, @"[^\d.\-]", "");
        if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return Task.FromResult<string?>(value);
        var decimals = config.Int("decimals") ?? 2;
        return Task.FromResult<string?>(d.ToString("F" + decimals, CultureInfo.InvariantCulture));
    }
}

/// <summary>date_normalize — config: { "format": "yyyy-MM-dd" }.</summary>
public sealed class DateNormalizeTransformer : IValueTransformer
{
    public string Type => "date_normalize";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return Task.FromResult<string?>(value);
        var format = config.Str("format") ?? "yyyy-MM-dd";
        return Task.FromResult<string?>(
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt.ToString(format, CultureInfo.InvariantCulture)
                : value);
    }
}

/// <summary>default — config: { "value": "..." } applied only when input is empty.</summary>
public sealed class DefaultValueTransformer : IValueTransformer
{
    public string Type => "default";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
        => Task.FromResult(string.IsNullOrWhiteSpace(value) ? config.Str("value") : value);
}

/// <summary>
/// ai_summary / translate — STUB AI transformers (mirror Drupal's OpenAI plugin).
/// Wire these to OpenAI / Azure OpenAI / Google Translate. Kept deterministic
/// here so the pipeline runs without external calls.
/// </summary>
public sealed class AiSummaryTransformer : IValueTransformer
{
    public string Type => "ai_summary";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
    {
        // Real impl: call chat completion with a "summarize" prompt over `value` (or ctx.FullText).
        if (string.IsNullOrWhiteSpace(value)) return Task.FromResult<string?>(value);
        var max = config.Int("maxChars") ?? 120;
        var summary = value.Length <= max ? value : value[..max] + "…";
        return Task.FromResult<string?>($"[summary] {summary}");
    }
}

public sealed class TranslateTransformer : IValueTransformer
{
    public string Type => "translate";
    public Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct)
    {
        // Real impl: call Google/Azure Translation to config["to"].
        var to = config.Str("to") ?? "en";
        return Task.FromResult(value is null ? null : $"[{to}] {value}");
    }
}
