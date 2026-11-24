using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PagesController : Controller
{
    [HttpGet("/")]
    public IActionResult Catalog() => View();

    [HttpGet("/index.html")]
    public IActionResult Index() => RedirectToAction(nameof(Catalog));

    [HttpGet("/dialogues/{id:int:min(1)}")]
    public IActionResult Editor(int id) => View(model: id);
}
