using DialogueSystem.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/dialogues/{id:int:min(1)}/analysis")]
public sealed class DialogueAnalysisController(IDialogueCatalog catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DialogueAnalysisReport>> Report(int id, CancellationToken ct)
    {
        var document = await catalog.ExportAsync(id, ct);
        return document is null ? NotFound() : Ok(DialogueRouteAnalysis.Analyze(document, ct));
    }

    [HttpGet("endings/{endingId:int:min(1)}")]
    public async Task<ActionResult<DialogueRouteResult>> Ending(int id, int endingId, CancellationToken ct)
    {
        var document = await catalog.ExportAsync(id, ct);
        if (document is null || !document.Lines.Any(line => line.Id == endingId && line.IsEnding))
            return NotFound();

        return Ok(DialogueRouteAnalysis.FindShortestRoute(document, endingId, ct));
    }
}
