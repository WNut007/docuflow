using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class MappingController(MappingRepository mapping) : Controller
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
