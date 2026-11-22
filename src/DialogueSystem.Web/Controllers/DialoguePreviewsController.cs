using System.Security.Claims;
using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}/previews")]
public sealed class DialoguePreviewsController(DialogueDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<PreviewSessionSummary>> List(int id, CancellationToken cancellationToken) =>
        await OwnedPreviews(id)
            .OrderByDescending(preview => preview.StartedAtUnixMilliseconds).ThenByDescending(preview => preview.Id)
            .Take(20)
            .Select(preview => new PreviewSessionSummary(preview.Id, preview.Step, preview.StartedAtUnixMilliseconds))
            .ToListAsync(cancellationToken);

    [HttpGet("{previewId:guid}/history")]
    public async Task<ActionResult<PreviewHistory>> History(int id, Guid previewId, CancellationToken cancellationToken)
    {
        var history = await OwnedPreviews(id).AsSingleQuery()
            .Where(preview => preview.Id == previewId)
            .Select(preview => new PreviewHistory(preview.Id, preview.Step,
                preview.History.OrderBy(entry => entry.Step)
                    .Select(entry => new PreviewHistoryStep(
                        entry.Step, entry.LineId, entry.Speaker, entry.Text, entry.IsEnding, entry.ChoiceText,
                        entry.StoryState)).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpPost]
    public async Task<ActionResult<PreviewStep>> StartPreview(
        int id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PreviewInput? input, CancellationToken cancellationToken)
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

        if (graph.MinimumPlayerLevel is int minimum && (input?.PlayerLevel is not int level || level < minimum))
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Detail = $"Для начала диалога требуется уровень {minimum} или выше.",
                Extensions = { ["minimumPlayerLevel"] = minimum }
            });
        }

        var preview = new DialoguePreview
        {
            Id = Guid.NewGuid(),
            DialogueId = id,
            CurrentLineId = graph.StartLineId!.Value,
            UserId = CurrentUserId(),
            StartedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var step = graph.GetPreviewStep(preview, validation.UnreachableLineIds);
        RecordHistory(preview, step, choiceText: null);
        db.Previews.Add(preview);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(step);
    }

    [HttpPost("{previewId:guid}/choices")]
    public async Task<ActionResult<PreviewStep>> AdvancePreview(
        int id, Guid previewId, SelectChoice request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var preview = await OwnedPreviews(id).SingleOrDefaultAsync(item => item.Id == previewId, cancellationToken);
        if (preview is null)
        {
            return NotFound();
        }

        if (request.Step != preview.Step)
        {
            return Conflict(new ProblemDetails { Detail = "Этот шаг предпросмотра уже изменился." });
        }

        var graph = (await DialogueGraphLoader.LoadAsync(db, id, cancellationToken))!;
        var validation = graph.Validate(preview);
        if (validation.Errors.Count != 0)
        {
            return UnprocessableEntity(validation);
        }

        var choice = graph.FindChoice(preview, request.ChoiceId);
        if (choice is null)
        {
            return BadRequest(new ProblemDetails { Detail = "Этот ответ недоступен в текущей реплике." });
        }

        preview.CurrentLineId = choice.TargetLineId;
        preview.StoryState = choice.NextState ?? preview.StoryState;
        preview.Step++;
        var step = graph.GetPreviewStep(preview, validation.UnreachableLineIds);
        RecordHistory(preview, step, choice.Text);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(step);
    }

    private IQueryable<DialoguePreview> OwnedPreviews(int dialogueId)
    {
        var userId = CurrentUserId();
        return db.Previews.Where(preview => preview.DialogueId == dialogueId && preview.UserId == userId);
    }

    private static void RecordHistory(DialoguePreview preview, PreviewStep step, string? choiceText)
    {
        preview.History.Add(new PreviewHistoryEntry
        {
            Step = step.Step,
            LineId = step.Line.Id,
            Speaker = step.Line.Speaker,
            Text = step.Line.Text,
            IsEnding = step.Line.IsEnding,
            ChoiceText = choiceText,
            StoryState = step.StoryState
        });
    }

    private int CurrentUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
