using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services.Export;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class ExportsController(IExportRepository exports, IExportQueue queue) : Controller
{
    public IActionResult Index()
        => View(new ExportsViewModel { Targets = exports.GetAllTargets(), RecentLogs = exports.GetRecentLogs(50) });

    /// <summary>Manual re-export: enqueue the document; the export job re-checks status before sending.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reexport(long documentId, CancellationToken ct)
    {
        await queue.EnqueueAsync(documentId, ct);
        TempData["ExportMsg"] = $"Re-export queued for document #{documentId}.";
        return RedirectToAction("Detail", "Documents", new { id = documentId });
    }
}
