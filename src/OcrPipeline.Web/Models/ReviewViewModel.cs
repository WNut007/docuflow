using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Models;

/// <summary>Backing model for the per-document accuracy review screen (Views/Documents/Review.cshtml).</summary>
public sealed class ReviewViewModel
{
    public required Document Document { get; init; }
    public bool HasResult { get; init; }            // false -> empty-state
    public bool NeedsReview { get; init; }
    public decimal? OverallConfidence { get; init; }
    public decimal Cutoff { get; init; }            // Ocr:MinPageConfidence
    public int PageCount { get; init; }
    public IReadOnlyList<ReviewValueModel> Values { get; init; } = Array.Empty<ReviewValueModel>();
}

public sealed class ReviewValueModel
{
    public long ResultValueId { get; init; }
    public string TargetProperty { get; init; } = "";
    public string? RawValue { get; init; }
    public string? NormalizedValue { get; init; }
    public decimal? Confidence { get; init; }
    public bool IsBelowThreshold { get; init; }

    /// <summary>"success" | "warning" | "danger" — computed server-side from the confidence band.</summary>
    public string BandClass { get; init; } = "secondary";

    /// <summary>Overlay block id ("block-1234") derived from SourceRef, or null when there's no source box.</summary>
    public string? BlockId { get; init; }
}

/// <summary>JSON body posted when a reviewer saves corrections.</summary>
public sealed class ReviewSavePayload
{
    public long DocumentId { get; set; }
    public List<ReviewCorrection> Corrections { get; set; } = new();
}

public sealed class ReviewCorrection
{
    public long ResultValueId { get; set; }
    public string? NormalizedValue { get; set; }
}
