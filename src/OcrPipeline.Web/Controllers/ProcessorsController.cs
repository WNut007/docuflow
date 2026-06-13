using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrPipeline.Web.Data;

namespace OcrPipeline.Web.Controllers;

[Authorize]
public sealed class ProcessorsController(ProcessorRepository processors) : Controller
{
    public IActionResult Index() => View(processors.GetAll());
}
