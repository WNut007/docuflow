namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// PURE template selection (no I/O). Multiple templates can exist for one DocumentType (e.g. a
/// single-page East Repair layout AND a 3-page invoice layout); this decides which one a document
/// uses so authoring one layout never clobbers another.
///
/// Selection is MANUAL and REQUIRED: the user picks a template at upload. The resolver only honours an
/// explicit <paramref name="chosenTemplateId"/> that is a real candidate of the document's type, and
/// returns null for anything else (no pick / wrong-type pick / unknown id / no candidates). It does
/// NOT guess from page count — page count belongs to <see cref="OcrPipeline.Web.Services.Zonal.PageRoleResolver"/>,
/// which assigns FIRST/CONTINUATION/LAST roles by physical page POSITION INSIDE an already-chosen
/// multi-page template. That is a different concept and is unaffected by this resolver.
/// </summary>
public static class TemplateResolver
{
    /// <summary>A template the resolver may pick. <see cref="IsMultiPage"/> = any field carries a
    /// page-role (FIRST/CONTINUATION/LAST). Candidates are already scoped to the document's type.</summary>
    public readonly record struct Candidate(int TemplateId, int DocumentTypeId, int Version, bool IsActive, bool IsMultiPage);

    /// <summary>The chosen template if it is a real candidate of this type; otherwise null (reject).
    /// No page-count guessing — the pick is required.</summary>
    public static int? Resolve(int? chosenTemplateId, IReadOnlyList<Candidate> candidates)
    {
        if (candidates is null || candidates.Count == 0) return null;

        // manual pick only — honoured solely when it's a real candidate of this type, else reject
        if (chosenTemplateId is { } chosen && candidates.Any(c => c.TemplateId == chosen))
            return chosen;

        return null;
    }
}
