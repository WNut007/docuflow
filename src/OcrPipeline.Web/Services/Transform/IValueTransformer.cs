using System.Text.Json;

namespace OcrPipeline.Web.Services.Transform;

/// <summary>A single configured step in a field's transformer pipeline.</summary>
public sealed class TransformerStep
{
    public int StepId { get; set; }
    public int FieldId { get; set; }
    public int StepOrder { get; set; }
    public string Type { get; set; } = "";
    public string? ConfigJson { get; set; }
    public bool IsActive { get; set; } = true;

    public JsonElement Config =>
        string.IsNullOrWhiteSpace(ConfigJson)
            ? default
            : JsonDocument.Parse(ConfigJson).RootElement;
}

/// <summary>Read-only context available to a transformer while it runs.</summary>
public sealed record TransformContext(
    string TargetProperty,
    IReadOnlyDictionary<string, string?> AllProperties,
    string FullText);

/// <summary>
/// A preprocessing plugin (mirrors Drupal's transformer plugins). Implementations
/// are registered in DI and resolved by <see cref="Type"/>. Stacking several in
/// order = the "Pipeline transformer".
/// </summary>
public interface IValueTransformer
{
    string Type { get; }
    Task<string?> ApplyAsync(string? value, JsonElement config, TransformContext ctx, CancellationToken ct);
}
