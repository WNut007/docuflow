using System.Collections.Generic;
using OcrPipeline.Web.Services.Mapping;
using Xunit;
using C = OcrPipeline.Web.Services.Mapping.TemplateResolver.Candidate;

namespace OcrPipeline.Tests;

/// <summary>
/// Pure, offline coverage of multi-template selection. Template selection is MANUAL and REQUIRED:
/// the user's explicit pick (a real candidate of the type) wins; anything else (no pick, wrong-type
/// pick, unknown id, no candidates) resolves to null. The resolver does NOT guess by page count —
/// that concept belongs to PageRoleResolver, which assigns FIRST/CONTINUATION/LAST by page position
/// INSIDE an already-chosen multi-page template. This is what stops two invoice layouts from sharing
/// one template and clobbering each other's zones.
/// </summary>
public sealed class TemplateResolverTests
{
    // type 1 has two layouts: tpl 2 = single-page (East Repair), tpl 1 = multi-page
    private static IReadOnlyList<C> InvoiceCandidates() => new[]
    {
        new C(TemplateId: 1, DocumentTypeId: 1, Version: 1, IsActive: true, IsMultiPage: true),
        new C(TemplateId: 2, DocumentTypeId: 1, Version: 1, IsActive: true, IsMultiPage: false),
    };

    [Fact]
    public void Manual_pick_of_a_real_candidate_wins()
    {
        // the user picked the single-page layout -> template 2; and the multi-page layout -> template 1
        Assert.Equal(2, TemplateResolver.Resolve(chosenTemplateId: 2, InvoiceCandidates()));
        Assert.Equal(1, TemplateResolver.Resolve(chosenTemplateId: 1, InvoiceCandidates()));
    }

    [Fact]
    public void No_pick_is_rejected_with_null()
    {
        // selection is required — no page-count guess to fall back on
        Assert.Null(TemplateResolver.Resolve(chosenTemplateId: null, InvoiceCandidates()));
    }

    [Fact]
    public void Pick_not_a_candidate_is_rejected_with_null()
    {
        // id 999 (wrong type / unknown) -> rejected, no fallback
        Assert.Null(TemplateResolver.Resolve(chosenTemplateId: 999, InvoiceCandidates()));
    }

    [Fact]
    public void No_templates_for_type_returns_null()
    {
        Assert.Null(TemplateResolver.Resolve(null, System.Array.Empty<C>()));
        Assert.Null(TemplateResolver.Resolve(chosenTemplateId: 5, System.Array.Empty<C>()));
    }
}
