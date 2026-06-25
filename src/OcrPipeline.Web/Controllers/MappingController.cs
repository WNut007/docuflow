using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;
using OcrPipeline.Web.Services.Zonal;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class MappingController(
    IMappingRepository mapping, IDocumentRepository documents, IDocumentIngestionService ingestion,
    ITableLayoutDetector tableDetector) : Controller
{
    public IActionResult Index()
    {
        ViewBag.DocTypes = mapping.GetDocumentTypes();
        return View(mapping.GetAllTemplates());
    }

    /// <summary>Create a new (empty) template for a document type, then open it in the zone designer.
    /// Lets a user author a NEW layout instead of editing (and clobbering) an existing one.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> CreateTemplate(int documentTypeId, string name, string? mappingMode,
        string? targetModel, IFormFile? sample, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) { TempData["Saved"] = "Template name is required."; return RedirectToAction(nameof(Index)); }
        // Orphan-proofing: only accept a real, active document type (the form is a dropdown, but a
        // forged/stale post could still send a bad id — reject it before the FK would).
        if (mapping.GetDocumentTypes().All(t => t.Id != documentTypeId))
        { TempData["Saved"] = "Please choose a valid document type."; return RedirectToAction(nameof(Index)); }
        // A template OWNS the sample its zones are drawn over — required at create time.
        if (sample is null || sample.Length == 0)
        { TempData["Saved"] = "Please choose a sample document to draw zones over."; return RedirectToAction(nameof(Index)); }

        string mode = string.Equals(mappingMode, "ZONAL", StringComparison.OrdinalIgnoreCase) ? "ZONAL" : "OCR_FIRST";
        int id = mapping.CreateTemplate(documentTypeId, name.Trim(),
            string.IsNullOrWhiteSpace(targetModel) ? "Invoice" : targetModel.Trim(), mode);

        // Store the sample as a drawing BACKDROP (SOURCE='SAMPLE', never enters the OCR pipeline) and
        // bind it to the template, so the designer draws on it with no doc-picker.
        long sampleId = await ingestion.StoreAndRasterizeAsync(
            sample, sourceChannel: "SAMPLE", statusCode: "SAMPLE",
            templateId: null, userId: GetUserId(), ocrLanguages: null, ct);
        mapping.SetTemplateSample(id, sampleId);

        return RedirectToAction(nameof(Zones), new { templateId = id });
    }

    private int? GetUserId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    // TODO: dead code — Edit (mapping editor) unlinked from UI as of review-polish; candidate for removal (ii).
    [HttpGet]
    public IActionResult Edit(int id)
    {
        var tpl = mapping.GetTemplateById(id);
        if (tpl is null) return NotFound();

        var steps = mapping.GetTransformerSteps(id);

        var vm = new TemplateEditViewModel
        {
            TemplateId = tpl.TemplateId,
            Name = tpl.Name,
            TargetModel = tpl.TargetModel,
            DocumentTypeId = tpl.DocumentTypeId,
            PropertyKeys = mapping.GetPropertyKeysForType(tpl.DocumentTypeId),
            Fields = tpl.Fields.Select(f => new FieldEditModel
            {
                FieldId = f.FieldId,
                TargetProperty = f.TargetProperty,
                DataType = f.DataType,
                IsRequired = f.IsRequired,
                SourceType = f.SourceType,
                KeyPattern = f.KeyPattern,
                SourcePattern = f.SourcePattern,
                TableHeader = f.TableHeader,
                RowSelector = f.RowSelector,
                DefaultValue = f.DefaultValue,
                MinConfidence = f.MinConfidence,
                StepsText = StepsToText(steps.GetValueOrDefault(f.FieldId))
            }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(TemplateEditViewModel vm)
    {
        // keep only rows with a target property
        var rows = vm.Fields.Where(f => !string.IsNullOrWhiteSpace(f.TargetProperty)).ToList();

        var fields = rows.Select(f => new MappingField
        {
            FieldId = f.FieldId,
            TargetProperty = f.TargetProperty.Trim(),
            DataType = f.DataType,
            IsRequired = f.IsRequired,
            SourceType = f.SourceType,
            KeyPattern = Nullify(f.KeyPattern),
            SourcePattern = Nullify(f.SourcePattern),
            TableHeader = Nullify(f.TableHeader),
            RowSelector = Nullify(f.RowSelector),
            DefaultValue = Nullify(f.DefaultValue),
            MinConfidence = f.MinConfidence
        }).ToList();

        var stepsByRow = new Dictionary<int, List<TransformerStep>>();
        for (int i = 0; i < rows.Count; i++)
        {
            var parsed = ParseSteps(rows[i].StepsText);
            if (parsed.Count > 0) stepsByRow[i] = parsed;
        }

        mapping.SaveFields(vm.TemplateId, fields, stepsByRow);
        TempData["Saved"] = $"Saved {fields.Count} field mapping(s).";
        return RedirectToAction(nameof(Edit), new { id = vm.TemplateId });
    }

    // ---- Point-and-click mapping (Prompt 4) -------------------------------

    // TODO: dead code — Visual (point-and-click mapping) unlinked from UI as of review-polish; candidate for removal (ii).
    [HttpGet]
    public IActionResult Visual(int templateId, long? documentId)
    {
        var tpl = mapping.GetTemplateById(templateId);
        if (tpl is null) return NotFound();

        var columns = mapping.GetTableColumns(templateId);
        var docs = documents.GetByTypeWithPreviews(tpl.DocumentTypeId);
        long? docId = documentId ?? docs.FirstOrDefault()?.DocumentId;
        int pageCount = docs.FirstOrDefault(d => d.DocumentId == docId)?.PageCount ?? 0;

        var templateOptions = mapping.GetAllTemplates()
            .Where(t => t.tpl.IsActive)
            .Select(t => new TemplateOption(t.tpl.TemplateId, $"{t.docType} — {t.tpl.Name}", t.tpl.TemplateId == templateId))
            .ToList();

        var vm = new VisualMappingViewModel
        {
            TemplateId = tpl.TemplateId,
            Name = tpl.Name,
            TargetModel = tpl.TargetModel,
            DocumentTypeId = tpl.DocumentTypeId,
            DocumentId = docId,
            PageCount = pageCount,
            TemplateOptions = templateOptions,
            Documents = docs.Select(d => new DocumentOption(d.DocumentId, d.FileName, d.PageCount)).ToList(),
            Fields = tpl.Fields.Select(f => new VisualFieldModel
            {
                FieldId = f.FieldId,
                TargetProperty = f.TargetProperty,
                DataType = f.DataType,
                IsRequired = f.IsRequired,
                SourceType = f.SourceType,
                TableHeader = f.TableHeader,
                RowSelector = f.RowSelector,
                DefaultValue = f.DefaultValue,
                MinConfidence = f.MinConfidence,
                BindingLabel = BindingLabel(f),
                SubColumns = (columns.GetValueOrDefault(f.FieldId) ?? new List<MappingTableColumn>())
                    .Select(c => new VisualSubColumnModel
                    {
                        ColumnId = c.ColumnId,
                        TargetSubProperty = c.TargetSubProperty,
                        DataType = c.DataType,
                        TableHeader = c.TableHeader,
                        SortOrder = c.SortOrder
                    }).ToList()
            }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult VisualSave([FromBody] VisualSavePayload payload)
    {
        if (payload is null || payload.TemplateId <= 0) return BadRequest();

        // PARTIAL upsert: the client only sends fields the user actually changed. Fields that aren't
        // in the payload are never touched, so their KeyPattern/SourcePattern/TableHeader and
        // transformer steps are preserved. (Full field editing lives in the Edit screen.)
        var rows = payload.Fields.Where(f => !string.IsNullOrWhiteSpace(f.TargetProperty)).ToList();
        int saved = 0;

        foreach (var f in rows)
        {
            var field = new MappingField
            {
                FieldId = f.FieldId,
                TargetProperty = f.TargetProperty.Trim(),
                DataType = f.DataType,
                IsRequired = f.IsRequired,
                SourceType = f.SourceType,
                // KeyPattern is derived server-side from the clicked key; never user-entered regex
                KeyPattern = f.SourceType == "KEY_VALUE" && !string.IsNullOrWhiteSpace(f.BindingKey)
                    ? BindingInference.KeyPatternFor(f.BindingKey) : null,
                SourcePattern = null,
                TableHeader = Nullify(f.TableHeader),
                RowSelector = Nullify(f.RowSelector),
                DefaultValue = Nullify(f.DefaultValue),
                MinConfidence = f.MinConfidence
            };

            // new field always rewrites its binding; existing field only when the user (re)bound/unbound it
            bool bindingChanged = f.FieldId <= 0 || f.BindingChanged;
            int fieldId = mapping.UpsertFieldBinding(payload.TemplateId, field, bindingChanged);
            saved++;

            // only replace sub-columns when the user changed them
            if (f.SubColumnsChanged)
            {
                var cols = f.SubColumns
                    .Where(c => !string.IsNullOrWhiteSpace(c.TargetSubProperty))
                    .Select(c => new MappingTableColumn
                    {
                        FieldId = fieldId,
                        TargetSubProperty = c.TargetSubProperty.Trim(),
                        DataType = c.DataType,
                        TableHeader = Nullify(c.TableHeader),
                        SortOrder = c.SortOrder,
                        IsActive = true
                    });
                mapping.SaveTableColumns(fieldId, cols);
            }
        }

        return Json(new { ok = true, saved });
    }

    // ---- Zone designer (template-based / zonal OCR) -----------------------

    [HttpGet]
    public IActionResult Zones(int templateId)
    {
        var tpl = mapping.GetTemplateById(templateId);
        if (tpl is null) return NotFound();

        // The designer draws on the template's OWN bound sample (no doc-picker). NULL => empty-state.
        long? docId = tpl.SampleDocumentId;
        int pageCount = docId is long sample ? documents.GetById(sample)?.PageCount ?? 0 : 0;

        // list ALL templates (not only active) so any layout can be edited without one clobbering another
        var templateOptions = mapping.GetAllTemplates()
            .Select(t => new TemplateOption(t.tpl.TemplateId, $"{t.docType} — {t.tpl.Name}", t.tpl.TemplateId == templateId))
            .ToList();

        var columnsByField = mapping.GetTableColumns(templateId); // line_item sub-columns for table fields

        var vm = new ZoneDesignerViewModel
        {
            TemplateId = tpl.TemplateId,
            Name = tpl.Name,
            MappingMode = tpl.MappingMode,
            DocumentTypeId = tpl.DocumentTypeId,
            DocumentId = docId,
            PageCount = pageCount,
            TemplateOptions = templateOptions,
            Fields = tpl.Fields.Select(f => new ZoneFieldModel
            {
                FieldId = f.FieldId,
                TargetProperty = f.TargetProperty,
                DataType = f.DataType,
                IsRequired = f.IsRequired,
                MinConfidence = f.MinConfidence,
                SourceType = f.SourceType,
                ZonePage = f.ZonePage,
                ZoneX = f.ZoneX, ZoneY = f.ZoneY, ZoneW = f.ZoneW, ZoneH = f.ZoneH,
                ZoneOcrHint = f.ZoneOcrHint, ZonePsm = f.ZonePsm, ZonePageRole = f.ZonePageRole,
                Columns = (columnsByField.TryGetValue(f.FieldId, out var cs) ? cs : new())
                    .OrderBy(c => c.SortOrder)
                    .Select(c => new ZoneColumnModel
                    {
                        ColumnId = c.ColumnId,
                        TargetSubProperty = c.TargetSubProperty,
                        DataType = c.DataType,
                        SortOrder = c.SortOrder,
                        ColXStart = c.ColXStart, ColXEnd = c.ColXEnd, IsAnchor = c.IsAnchor,
                        LineSelectMode = c.LineSelectMode, LineSelectIndices = c.LineSelectIndices,
                        LineJoinSeparator = c.LineJoinSeparator
                    }).ToList()
            }).ToList()
        };
        return View(vm);
    }

    /// <summary>Option ③-B "rough-box → auto-columns": given the table zone the user drew, propose its
    /// column separators from the OCR table structure (engine-agnostic <see cref="ITableLayoutDetector"/>).
    /// Detects on the template's OWN bound sample (resolved server-side, never a client-supplied doc id).
    /// Always 200s with a (possibly empty) result + note — the designer stays usable when the detector is
    /// offline/disabled. Same auth + antiforgery posture as <see cref="ZonesSave"/>.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DetectTables([FromBody] DetectTablesPayload payload, CancellationToken ct)
    {
        if (payload is null || payload.TemplateId <= 0) return BadRequest();
        var tpl = mapping.GetTemplateById(payload.TemplateId);
        if (tpl is null) return NotFound();

        if (tpl.SampleDocumentId is not long sampleId)
            return Json(new { ok = true, boundaries = Array.Empty<double>(), columnCount = 0,
                              note = "This template has no sample document to detect on." });
        if (payload.ZoneW <= 0 || payload.ZoneH <= 0)
            return Json(new { ok = true, boundaries = Array.Empty<double>(), columnCount = 0,
                              note = "Draw a table zone first, then Auto-detect." });

        int page = payload.Page < 1 ? 1 : payload.Page;
        var result = await tableDetector.DetectColumnsAsync(
            sampleId, page, new RectN(payload.ZoneX, payload.ZoneY, payload.ZoneW, payload.ZoneH), ct);
        return Json(new { ok = true, boundaries = result.PageColumnBoundariesX,
                          columnCount = result.ColumnCount, note = result.Note });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ZonesSave([FromBody] ZonesSavePayload payload)
    {
        if (payload is null || payload.TemplateId <= 0) return BadRequest();

        string mode = string.Equals(payload.MappingMode, "ZONAL", StringComparison.OrdinalIgnoreCase)
            ? "ZONAL" : "OCR_FIRST";

        bool IsTable(ZoneFieldPayload f) => string.Equals(f.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase);
        bool HasZone(ZoneFieldPayload f) => !string.IsNullOrWhiteSpace(f.TargetProperty) && f.ZoneX is not null;

        // Guard (belt-and-suspenders to the designer UX): a multi-page line_item table is up to three
        // role-tagged TABLE_CELL regions SHARING one TargetProperty. Reject duplicate roles / a
        // multi-region table missing a role on any region before anything is persisted.
        var validation = ZonalSaveValidator.Validate(payload.Fields
            .Where(f => HasZone(f) && IsTable(f))
            .Select(f => new ZonalSaveValidator.TableFieldInfo(f.TargetProperty.Trim(), f.ZonePageRole)));
        if (!validation.IsValid)
            return BadRequest(new { ok = false, error = validation.Error });

        // Remove regions the user deleted (e.g. a redundant CONTINUATION) before upserting the rest.
        if (payload.RemovedFieldIds.Count > 0)
            mapping.DeleteZoneFields(payload.TemplateId, payload.RemovedFieldIds);

        // Scalar zones (name + drawn zone), persisted together; this call also sets the MappingMode.
        var fields = payload.Fields
            .Where(f => HasZone(f) && !IsTable(f))
            .Select(f => new MappingField
            {
                FieldId = f.FieldId,
                TargetProperty = f.TargetProperty.Trim(),
                DataType = f.DataType,
                IsRequired = f.IsRequired,
                MinConfidence = f.MinConfidence,
                SourceType = "KEY_VALUE",
                ZonePage = f.ZonePage ?? 1,
                ZoneX = f.ZoneX, ZoneY = f.ZoneY, ZoneW = f.ZoneW, ZoneH = f.ZoneH,
                ZoneOcrHint = NormalizeHint(f.ZoneOcrHint),
                ZonePsm = f.ZonePsm,
                ZonePageRole = NormalizeRole(f.ZonePageRole)
            }).ToList();

        mapping.SaveZones(payload.TemplateId, mode, fields);

        // Table (line_item) fields: the zone rect is the table; its columns carry x-boundaries + rules.
        int tables = 0;
        foreach (var f in payload.Fields.Where(f => HasZone(f) && IsTable(f)))
        {
            var tableField = new MappingField
            {
                FieldId = f.FieldId,
                TargetProperty = f.TargetProperty.Trim(),
                DataType = string.IsNullOrWhiteSpace(f.DataType) ? "STRING" : f.DataType,
                IsRequired = f.IsRequired,
                MinConfidence = f.MinConfidence,
                SourceType = "TABLE_CELL",
                ZonePage = f.ZonePage ?? 1,
                ZoneX = f.ZoneX, ZoneY = f.ZoneY, ZoneW = f.ZoneW, ZoneH = f.ZoneH,
                ZoneOcrHint = NormalizeHint(f.ZoneOcrHint),
                ZonePsm = f.ZonePsm,
                ZonePageRole = NormalizeRole(f.ZonePageRole)
            };
            var cols = (f.Columns ?? new())
                .Where(c => !string.IsNullOrWhiteSpace(c.TargetSubProperty))
                .Select((c, i) => new MappingTableColumn
                {
                    TargetSubProperty = c.TargetSubProperty.Trim(),
                    DataType = string.IsNullOrWhiteSpace(c.DataType) ? "STRING" : c.DataType,
                    SortOrder = c.SortOrder == 0 ? i : c.SortOrder,
                    IsActive = true,
                    ColXStart = c.ColXStart, ColXEnd = c.ColXEnd, IsAnchor = c.IsAnchor,
                    LineSelectMode = c.LineSelectMode, LineSelectIndices = c.LineSelectIndices,
                    LineJoinSeparator = c.LineJoinSeparator
                }).ToList();
            mapping.SaveTableZone(payload.TemplateId, tableField, cols);
            tables++;
        }

        return Json(new { ok = true, mode, saved = fields.Count, tables });
    }

    private static string NormalizeHint(string? hint) => (hint ?? "TEXT").Trim().ToUpperInvariant() switch
    {
        "NUMERIC" => "NUMERIC",
        "DATE" => "DATE",
        "INT" => "INT",
        _ => "TEXT"
    };

    /// <summary>Multi-page page-role (Phase 3). Unknown/blank -> null (single-page/legacy behaviour).</summary>
    private static string? NormalizeRole(string? role) => (role ?? "").Trim().ToUpperInvariant() switch
    {
        "FIRST" => "FIRST",
        "CONTINUATION" or "CONT" => "CONTINUATION",
        "LAST" => "LAST",
        _ => null
    };

    /// <summary>User-friendly binding summary (never a raw regex).</summary>
    private static string? BindingLabel(MappingField f) => f.SourceType switch
    {
        "KEY_VALUE" => f.KeyPattern is null ? null : UnpatternKey(f.KeyPattern),
        "TABLE_CELL" => f.TableHeader,
        "CONSTANT" => f.DefaultValue,
        _ => null
    };

    private static string UnpatternKey(string pattern)
    {
        var p = pattern;
        if (p.StartsWith('^')) p = p[1..];
        if (p.EndsWith('$')) p = p[..^1];
        try { return Regex.Unescape(p); } catch (RegexParseException) { return p; }
    }

    // ---- helpers ----------------------------------------------------------
    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? StepsToText(List<TransformerStep>? steps)
    {
        if (steps is null || steps.Count == 0) return null;
        return string.Join('\n', steps
            .OrderBy(s => s.StepOrder)
            .Select(s => string.IsNullOrWhiteSpace(s.ConfigJson) ? s.Type : $"{s.Type}|{s.ConfigJson}"));
    }

    private static List<TransformerStep> ParseSteps(string? text)
    {
        var list = new List<TransformerStep>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('|', 2);
            var type = parts[0].Trim();
            if (type.Length == 0) continue;
            list.Add(new TransformerStep
            {
                Type = type,
                ConfigJson = parts.Length > 1 ? parts[1].Trim() : null
            });
        }
        return list;
    }
}
