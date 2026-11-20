using System.Security.Claims;
using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}/previews")]
public sealed class DialoguePreviewsController(DialogueDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<PreviewStep>> StartPreview(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var graph = await DialogueGraphLoader.LoadAsync(db, id, cancellationToken);
        if (graph is null)
        {
            return NotFound();
        }

        var validation = graph.Validate();
        if (validation.Errors.Count != 0)
        {
            return UnprocessableEntity(validation);
        }

        var preview = new DialoguePreview
        {
            Id = Guid.NewGuid(),
            DialogueId = id,
            CurrentLineId = graph.StartLineId!.Value,
            UserId = CurrentUserId()
        };
        db.Previews.Add(preview);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(graph.GetPreviewStep(preview, validation.UnreachableLineIds));
    }

    [HttpPost("{previewId:guid}/choices")]
    public async Task<ActionResult<PreviewStep>> AdvancePreview(
        int id, Guid previewId, SelectChoice request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var userId = CurrentUserId();
        var preview = await db.Previews.SingleOrDefaultAsync(
            item => item.Id == previewId && item.DialogueId == id && item.UserId == userId, cancellationToken);
        if (preview is null)
        {
            return NotFound();
        }

        if (request.Step != preview.Step)
        {
            return Conflict(new ProblemDetails { Detail = "Этот шаг предпросмотра уже изменился." });
        }

        var graph = (await DialogueGraphLoader.LoadAsync(db, id, cancellationToken))!;
        var validation = graph.Validate();
        if (validation.Errors.Count != 0)
        {
            return UnprocessableEntity(validation);
        }

        var choice = graph.FindChoice(preview.CurrentLineId, request.ChoiceId);
        if (choice is null)
        {
            return BadRequest(new ProblemDetails { Detail = "Этот ответ недоступен в текущей реплике." });
        }

        preview.CurrentLineId = choice.TargetLineId;
        preview.Step++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(graph.GetPreviewStep(preview, validation.UnreachableLineIds));
    }

    private int CurrentUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
