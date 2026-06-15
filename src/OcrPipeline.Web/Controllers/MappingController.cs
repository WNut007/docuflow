using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class MappingController(IMappingRepository mapping, IDocumentRepository documents) : Controller
{
    public IActionResult Index()
        => View(mapping.GetAllTemplates());

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
    public IActionResult Zones(int templateId, long? documentId)
    {
        var tpl = mapping.GetTemplateById(templateId);
        if (tpl is null) return NotFound();

        var docs = documents.GetByTypeWithPreviews(tpl.DocumentTypeId);
        long? docId = documentId ?? docs.FirstOrDefault()?.DocumentId;
        int pageCount = docs.FirstOrDefault(d => d.DocumentId == docId)?.PageCount ?? 0;

        var templateOptions = mapping.GetAllTemplates()
            .Where(t => t.tpl.IsActive)
            .Select(t => new TemplateOption(t.tpl.TemplateId, $"{t.docType} — {t.tpl.Name}", t.tpl.TemplateId == templateId))
            .ToList();

        var vm = new ZoneDesignerViewModel
        {
            TemplateId = tpl.TemplateId,
            Name = tpl.Name,
            MappingMode = tpl.MappingMode,
            DocumentTypeId = tpl.DocumentTypeId,
            DocumentId = docId,
            PageCount = pageCount,
            TemplateOptions = templateOptions,
            Documents = docs.Select(d => new DocumentOption(d.DocumentId, d.FileName, d.PageCount)).ToList(),
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
                ZoneOcrHint = f.ZoneOcrHint, ZonePsm = f.ZonePsm
            }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ZonesSave([FromBody] ZonesSavePayload payload)
    {
        if (payload is null || payload.TemplateId <= 0) return BadRequest();

        string mode = string.Equals(payload.MappingMode, "ZONAL", StringComparison.OrdinalIgnoreCase)
            ? "ZONAL" : "OCR_FIRST";

        // Persist only fields that have both a name and a drawn zone.
        var fields = payload.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.TargetProperty) && f.ZoneX is not null)
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
                ZonePsm = f.ZonePsm
            }).ToList();

        mapping.SaveZones(payload.TemplateId, mode, fields);
        return Json(new { ok = true, mode, saved = fields.Count });
    }

    private static string NormalizeHint(string? hint) => (hint ?? "TEXT").Trim().ToUpperInvariant() switch
    {
        "NUMERIC" => "NUMERIC",
        "DATE" => "DATE",
        "INT" => "INT",
        _ => "TEXT"
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
