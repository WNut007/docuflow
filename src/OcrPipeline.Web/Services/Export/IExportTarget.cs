using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Export;

/// <summary>Result of one export attempt. Snippet is a truncated response — never a secret/signature.</summary>
public sealed record ExportAttempt(bool Success, int? HttpStatus, string? ResponseSnippet);

/// <summary>
/// An export destination resolved by <see cref="Kind"/> (mirrors IOcrEngine / IValueTransformer).
/// Implementations push the mapped JSON downstream and report the attempt.
/// </summary>
public interface IExportTarget
{
    string Kind { get; }
    Task<ExportAttempt> SendAsync(Document document, string mappedJson, ExportTarget target, CancellationToken ct);
}

/// <summary>Bound from configuration section "Ocr:Export".</summary>
public sealed class ExportOptions
{
    public int MaxConcurrency { get; set; } = 2;
    public int Capacity { get; set; } = 1000;
    /// <summary>Per-request outbound HTTP timeout (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
