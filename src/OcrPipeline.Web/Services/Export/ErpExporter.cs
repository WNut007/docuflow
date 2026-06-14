using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Export;

/// <summary>
/// STUB ERP exporter — a clear extension point. There is no real ERP integration: it returns a
/// non-success attempt so a document whose only target is ERP is NOT marked CONSUMED. Replace
/// SendAsync with a real ERP client (SOAP/REST/SDK) to enable it.
/// </summary>
public sealed class ErpExporter : IExportTarget
{
    public string Kind => "ERP";

    public Task<ExportAttempt> SendAsync(Document document, string mappedJson, ExportTarget target, CancellationToken ct)
        => Task.FromResult(new ExportAttempt(false, null, "ERP exporter is a stub (extension point) — not implemented"));
}
