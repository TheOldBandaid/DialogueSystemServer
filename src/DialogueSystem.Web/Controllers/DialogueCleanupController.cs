using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}/cleanup")]
public sealed class DialogueCleanupController(DialogueCleanupService cleanup) : ControllerBase
{
    [HttpPost("plan")]
    public async Task<IActionResult> Plan(int id, CleanupCriteria criteria, CancellationToken ct) =>
        Result(await cleanup.PlanAsync(id, SessionPrincipal.UserId(User), criteria, ct));

    [HttpPost]
    public async Task<IActionResult> Apply(int id, CleanupInput input, CancellationToken ct)
    {
        if (input.ConfirmationToken is null)
            return StatusCode(StatusCodes.Status428PreconditionRequired, new ProblemDetails { Detail = "Сначала рассчитайте и подтвердите состав очистки." });
        return Result(await cleanup.ApplyAsync(id, SessionPrincipal.UserId(User), input.Criteria, input.ConfirmationToken, ct));
    }

    private IActionResult Result(CleanupOutcome outcome) => outcome.Status switch
    {
        CleanupStatus.Ready => Ok((object?)outcome.Plan ?? outcome.Summary),
        CleanupStatus.NotFound => NotFound(),
        CleanupStatus.Forbidden => Forbid(),
        CleanupStatus.Invalid => BadRequest(new ProblemDetails { Detail = outcome.Detail }),
        CleanupStatus.Stale => Conflict(new ProblemDetails { Detail = outcome.Detail }),
        _ => throw new InvalidOperationException()
    };

    public sealed record CleanupInput([Required] CleanupCriteria Criteria,
        [StringLength(64, MinimumLength = 64)] string? ConfirmationToken);
}
