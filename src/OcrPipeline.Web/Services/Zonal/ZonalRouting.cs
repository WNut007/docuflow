using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Zonal;

/// <summary>Pure routing decision (Phase 3): does a ZONAL template use the multi-page extraction path?
/// Multi-page when ANY field carries a page-role (FIRST/CONTINUATION/LAST); a template with all-null
/// roles stays on the unchanged single-page <see cref="ZonalExtractionService.ProcessAsync"/>.</summary>
public static class ZonalRouting
{
    public static bool IsMultiPage(MappingTemplate template)
        => template.Fields.Any(f => !string.IsNullOrWhiteSpace(f.ZonePageRole));
}
