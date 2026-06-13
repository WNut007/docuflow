namespace OcrPipeline.Web.Services.Transform;

/// <summary>
/// Runs an ordered list of transformer steps over a single value.
/// This is the "Pipeline transformer": value -> step1 -> step2 -> ... -> result.
/// </summary>
public sealed class TransformerPipeline
{
    private readonly IReadOnlyDictionary<string, IValueTransformer> _byType;

    public TransformerPipeline(IEnumerable<IValueTransformer> transformers)
        => _byType = transformers.ToDictionary(t => t.Type, StringComparer.OrdinalIgnoreCase);

    public async Task<string?> RunAsync(
        string? value,
        IReadOnlyList<TransformerStep> steps,
        TransformContext ctx,
        CancellationToken ct = default)
    {
        foreach (var step in steps.Where(s => s.IsActive).OrderBy(s => s.StepOrder))
        {
            if (_byType.TryGetValue(step.Type, out var transformer))
                value = await transformer.ApplyAsync(value, step.Config, ctx, ct);
            // unknown step types are skipped (forward-compatible)
        }
        return value;
    }
}
