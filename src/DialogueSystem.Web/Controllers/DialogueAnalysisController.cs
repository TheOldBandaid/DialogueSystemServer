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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DialogueAnalysisReport>> Report(int id, CancellationToken ct)
    {
        var report = await catalog.AnalyzeAsync(id, ct);
        return report is null ? NotFound() : Ok(report);
    }

    [HttpGet("endings/{endingId:int:min(1)}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DialogueRouteResult>> Ending(int id, int endingId, CancellationToken ct)
    {
        var route = await catalog.FindShortestRouteAsync(id, endingId, ct);
        return route is null ? NotFound() : Ok(route);
    }
}
