namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// PURE save-time guard for zone-designer TABLE_CELL fields (Phase 3, option-a authoring UX). The
/// designer already prevents divergent names by construction (a multi-page table's name lives once on
/// the table, not on each page-region), so the only machine-checkable invariants left — and the ones
/// that can be reached by a hand-crafted API payload — are enforced here:
///   - within one table (grouped by TargetProperty), no two regions may share a page-role;
///   - a table with more than one region must give EVERY region a role (FIRST/CONTINUATION/LAST);
///     a single-region table may be role-less (single-page/legacy).
/// "Reject divergent names across role-siblings" is intentionally NOT attempted: in the flat model the
/// group key IS the name, so the check would be circular — divergence is prevented in the UI instead.
/// No I/O, so it is unit-testable on plain strings (see ZonalSaveValidatorTests).
/// </summary>
public static class ZonalSaveValidator
{
    /// <summary>One TABLE_CELL field being saved: its table name and raw page-role (scalars excluded).</summary>
    public readonly record struct TableFieldInfo(string? TargetProperty, string? Role);

    public sealed record Result(bool IsValid, string? Error)
    {
        public static readonly Result Ok = new(true, null);
        public static Result Fail(string message) => new(false, message);
    }

    /// <summary>Validate the table-region layout. Pass ONLY the TABLE_CELL fields (one entry per region).</summary>
    public static Result Validate(IEnumerable<TableFieldInfo> tableFields)
    {
        foreach (var group in tableFields.GroupBy(
                     f => (f.TargetProperty ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var roles = group.Select(f => NormalizeRole(f.Role)).ToList();
            if (roles.Count <= 1) continue;   // a single region is always fine (single-page or a lone role)

            string name = string.IsNullOrWhiteSpace(group.Key) ? "(unnamed)" : group.Key;

            if (roles.Any(r => r is null))
                return Result.Fail(
                    $"Table “{name}” has multiple page-regions, so each must have a page-role " +
                    "(First / Continuation / Last).");

            var dup = roles.GroupBy(r => r).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
                return Result.Fail(
                    $"Table “{name}” has two “{dup.Key}” page-regions. " +
                    "Each region needs a distinct role (First / Continuation / Last).");
        }
        return Result.Ok;
    }

    /// <summary>Canonical page-role token, or null for blank/unknown (single-page/legacy). Mirrors
    /// MappingController.NormalizeRole so the guard and persistence agree on what a role is.</summary>
    public static string? NormalizeRole(string? role) => (role ?? "").Trim().ToUpperInvariant() switch
    {
        "FIRST" => "FIRST",
        "CONTINUATION" or "CONT" => "CONTINUATION",
        "LAST" => "LAST",
        _ => null
    };
}
