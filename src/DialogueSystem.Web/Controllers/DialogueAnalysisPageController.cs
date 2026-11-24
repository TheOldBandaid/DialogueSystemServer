using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web.Controllers;

[ApiExplorerSettings(IgnoreApi = true)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DialogueAnalysisPageController : Controller
{
    [HttpGet("/dialogues/{id:int:min(1)}/analysis")]
    public IActionResult Index(int id) => View(model: id);
}
