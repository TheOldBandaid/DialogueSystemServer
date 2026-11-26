using DialogueSystem.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.ComponentModel.DataAnnotations;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}/previews")]
public sealed class DialoguePreviewsController(PreviewService previews) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PreviewSessionSummary>> List(int id, CancellationToken cancellationToken) =>
        previews.ListAsync(id, SessionPrincipal.UserId(User), cancellationToken);

    [HttpGet("page")]
    public async Task<ActionResult<PreviewSessionPage>> Page(int id, Guid? before, CancellationToken cancellationToken)
    {
        var page = await previews.ListPageAsync(id, SessionPrincipal.UserId(User), before, cancellationToken);
        return page is null ? NotFound() : Ok(page);
    }

    [HttpGet("{previewId:guid}/history")]
    public async Task<ActionResult<PreviewHistory>> History(int id, Guid previewId, CancellationToken cancellationToken,
        [FromQuery, Range(-1, int.MaxValue)] int after = -1)
    {
        var history = await previews.HistoryAsync(id, SessionPrincipal.UserId(User), previewId, cancellationToken, after);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpPost]
    public async Task<ActionResult<PreviewStep>> StartPreview(
        int id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PreviewInput? input,
        CancellationToken cancellationToken) =>
        PreviewResponse(await previews.StartAsync(id, SessionPrincipal.UserId(User), input?.PlayerLevel, cancellationToken));

    [HttpPost("{previewId:guid}/choices")]
    public async Task<ActionResult<PreviewStep>> AdvancePreview(
        int id, Guid previewId, SelectChoice request, CancellationToken cancellationToken) =>
        PreviewResponse(await previews.AdvanceAsync(id, SessionPrincipal.UserId(User), previewId, request, cancellationToken));

    private ActionResult PreviewResponse(PreviewResult result) => result.Status switch
    {
        PreviewStatus.Ready => Ok(result.Step),
        PreviewStatus.NotFound => NotFound(),
        PreviewStatus.InvalidGraph => UnprocessableEntity(result.Validation),
        PreviewStatus.LevelTooLow => UnprocessableEntity(new ProblemDetails
        {
            Detail = $"Для начала диалога требуется уровень {result.MinimumLevel} или выше.",
            Extensions = { ["minimumPlayerLevel"] = result.MinimumLevel }
        }),
        PreviewStatus.StaleStep => Conflict(new ProblemDetails { Detail = "Этот шаг предпросмотра уже изменился." }),
        PreviewStatus.StaleGraph => Conflict(new ProblemDetails { Detail = "Диалог изменился во время запроса. Повторите действие." }),
        PreviewStatus.RemovedLine => Conflict(new ProblemDetails
        {
            Detail = "Текущая реплика удалена. Начните новое прохождение. Сохранённая история остаётся доступной."
        }),
        PreviewStatus.UnavailableChoice => BadRequest(new ProblemDetails
        {
            Detail = "Этот ответ недоступен в текущей реплике."
        }),
        _ => throw new InvalidOperationException()
    };
}
