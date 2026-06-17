using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Locks the multi-page routing predicate (Phase 3): all-null page-roles -> legacy
/// single-page path; any role set -> multi-page path.</summary>
public sealed class ZonalRoutingTests
{
    private static MappingTemplate With(params string?[] roles)
        => new() { TemplateId = 1, Fields = roles.Select((r, i) => new MappingField { FieldId = i + 1, ZonePageRole = r }).ToList() };

    [Fact]
    public void All_null_roles_is_single_page_legacy()
    {
        Assert.False(ZonalRouting.IsMultiPage(With(null, null)));   // East Repair / thai-invoice shape
        Assert.False(ZonalRouting.IsMultiPage(With("", "  ")));      // blank treated as null
        Assert.False(ZonalRouting.IsMultiPage(With()));             // no fields
    }

    [Theory]
    [InlineData("FIRST")]
    [InlineData("CONTINUATION")]
    [InlineData("LAST")]
    public void Any_role_set_is_multi_page(string role)
        => Assert.True(ZonalRouting.IsMultiPage(With(null, role)));
}
