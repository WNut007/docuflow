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
    /// <summary>Per-physical-page line_item table zone (multi-page), for the row->page image highlight.</summary>
    public IReadOnlyList<ReviewPageZone> PageTableZones { get; init; } = Array.Empty<ReviewPageZone>();
}

public sealed class ReviewValueModel
{
    public long ResultValueId { get; init; }
    public int FieldId { get; init; }
    public string TargetProperty { get; init; } = "";
    public string? RawValue { get; init; }
    public string? NormalizedValue { get; init; }
    public decimal? Confidence { get; init; }
    public bool IsBelowThreshold { get; init; }

    /// <summary>"success" | "warning" | "danger" — computed server-side from the confidence band.</summary>
    public string BandClass { get; init; } = "secondary";

    /// <summary>Overlay block id ("block-1234") derived from SourceRef, or null when there's no source box.</summary>
    public string? BlockId { get; init; }

    // Zone rectangle (normalized 0..1) from the field's drawn zone, for the focus->highlight overlay.
    public decimal? ZoneX { get; init; }
    public decimal? ZoneY { get; init; }
    public decimal? ZoneW { get; init; }
    public decimal? ZoneH { get; init; }
    public int ZonePage { get; init; } = 1;

    /// <summary>Non-null only for the line_item TABLE_CELL field: its columns + parsed display rows.</summary>
    public ReviewTableModel? Table { get; init; }
}

/// <summary>A line_item field rendered as ONE editable table on the review screen (multi-page rows
/// concatenated). <see cref="RowPages"/> is parallel to <see cref="Rows"/>.</summary>
public sealed class ReviewTableModel
{
    public IReadOnlyList<ReviewColumn> Columns { get; init; } = Array.Empty<ReviewColumn>();
    /// <summary>One row per line item; each cell a DISPLAY string keyed by sub-property (scale-preserving).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; }
        = Array.Empty<IReadOnlyDictionary<string, string>>();
    /// <summary>Source page per row (Phase 3 multi-page; all 1 for single-page), parallel to Rows.</summary>
    public IReadOnlyList<int> RowPages { get; init; } = Array.Empty<int>();
    /// <summary>Contiguous page runs for the compact jump strip.</summary>
    public IReadOnlyList<ReviewTablePageGroup> PageGroups { get; init; } = Array.Empty<ReviewTablePageGroup>();
    /// <summary>True when rows span more than one page -> show the Pg column + jump strip.</summary>
    public bool IsMultiPage { get; init; }
}

/// <summary>One contiguous run of rows from the same source page (jump-strip chip).</summary>
public sealed record ReviewTablePageGroup(int Page, int FirstRowIndex, int Count);

/// <summary>The table zone rect (normalized 0..1) owning a given physical page, for the row->page highlight.</summary>
public sealed class ReviewPageZone
{
    public int Page { get; init; }
    public decimal X { get; init; }
    public decimal Y { get; init; }
    public decimal W { get; init; }
    public decimal H { get; init; }
}

public sealed class ReviewColumn
{
    public string SubProperty { get; init; } = "";
    public string DataType { get; init; } = "STRING";
    public bool IsAnchor { get; init; }
}

/// <summary>JSON body posted when a reviewer saves corrections.</summary>
public sealed class ReviewSavePayload
{
    public long DocumentId { get; set; }
    public List<ReviewCorrection> Corrections { get; set; } = new();
    /// <summary>Edited line_item tables: each is re-typed server-side and written as typed JSON.</summary>
    public List<ReviewTableCorrection> TableCorrections { get; set; } = new();
}

public sealed class ReviewCorrection
{
    public long ResultValueId { get; set; }
    public string? NormalizedValue { get; set; }
}

public sealed class ReviewTableCorrection
{
    public long ResultValueId { get; set; }
    public int FieldId { get; set; }
    /// <summary>Edited cell strings, one dictionary (sub-property -> string) per row.</summary>
    public List<Dictionary<string, string>> Rows { get; set; } = new();
}
