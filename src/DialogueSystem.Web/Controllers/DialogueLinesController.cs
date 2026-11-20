using System.ComponentModel.DataAnnotations;
using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}")]
public sealed class DialogueLinesController(DialogueDbContext db) : ControllerBase
{
    [HttpGet("lines")]
    public async Task<ActionResult<List<LineSummary>>> List(int id, CancellationToken ct)
    {
        if (!await db.Dialogues.AnyAsync(dialogue => dialogue.Id == id, ct))
            return NotFound();

        return await db.Lines.Where(line => line.DialogueId == id).OrderBy(line => line.Id)
            .Select(line => new LineSummary(line.Id, line.Speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerId))
            .ToListAsync(ct);
    }

    [HttpPost("lines")]
    public async Task<ActionResult<LineSummary>> Create(int id, LineInput input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Dialogues.AnyAsync(dialogue => dialogue.Id == id, ct))
            return NotFound();

        var speaker = await db.Characters.FindAsync([input.SpeakerId], ct);
        if (speaker is null)
            return BadRequest(new ProblemDetails { Detail = "Говорящий персонаж не найден." });
        if (input.IsEnding && input.IsStoryBranch)
            return BadRequest(new ProblemDetails { Detail = "Концовка не может быть сюжетной развилкой." });

        var line = new DialogueLine { DialogueId = id, Speaker = speaker, Text = input.Text.Trim(),
            IsEnding = input.IsEnding, IsStoryBranch = input.IsStoryBranch };
        db.Lines.Add(line);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return CreatedAtAction(nameof(DialoguesController.Context), "Dialogues", new { id, lineId = line.Id },
            new LineSummary(line.Id, speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, speaker.Id));
    }

    [HttpPut("lines/{lineId:int}")]
    public async Task<IActionResult> Update(int id, int lineId, LineInput input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var line = await FindLine(id, lineId, ct);
        if (line is null)
            return NotFound();
        if (!await db.Characters.AnyAsync(character => character.Id == input.SpeakerId, ct))
            return BadRequest(new ProblemDetails { Detail = "Говорящий персонаж не найден." });
        if (input.IsEnding && input.IsStoryBranch)
            return BadRequest(new ProblemDetails { Detail = "Концовка не может быть сюжетной развилкой." });
        if (input.IsEnding && await db.Choices.AnyAsync(choice => choice.DialogueId == id && choice.SourceLineId == lineId, ct))
            return Conflict(new ProblemDetails { Detail = "Сначала удалите исходящие варианты ответа." });

        line.SpeakerId = input.SpeakerId;
        line.Text = input.Text.Trim();
        line.IsEnding = input.IsEnding;
        line.IsStoryBranch = input.IsStoryBranch;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPut("start")]
    public async Task<IActionResult> SetStart(int id, StartInput input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var dialogue = await db.Dialogues.FindAsync([id], ct);
        if (dialogue is null)
            return NotFound();
        if (input.LineId is int lineId && !await db.Lines.AnyAsync(line => line.Id == lineId && line.DialogueId == id, ct))
            return BadRequest(new ProblemDetails { Detail = "Стартовая реплика должна принадлежать этому диалогу." });

        dialogue.StartLineId = input.LineId;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("lines/{lineId:int}")]
    public async Task<IActionResult> Delete(int id, int lineId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var line = await FindLine(id, lineId, ct);
        if (line is null)
            return NotFound();
        if (await db.Dialogues.AnyAsync(dialogue => dialogue.Id == id && dialogue.StartLineId == lineId, ct) ||
            await db.Choices.AnyAsync(choice => choice.DialogueId == id && choice.TargetLineId == lineId &&
                choice.SourceLineId != lineId, ct))
            return Conflict(new ProblemDetails { Detail = "Перенесите старт или измените входящие переходы перед удалением." });

        await db.Choices.Where(choice => choice.DialogueId == id && choice.SourceLineId == lineId).ExecuteDeleteAsync(ct);
        db.Lines.Remove(line);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("lines/{lineId:int}/choices")]
    public Task<IActionResult> CreateChoice(int id, int lineId, ChoiceInput input, CancellationToken ct) =>
        SaveChoice(id, lineId, null, input, ct);

    [HttpPut("lines/{lineId:int}/choices/{choiceId:int}")]
    public Task<IActionResult> UpdateChoice(int id, int lineId, int choiceId, ChoiceInput input, CancellationToken ct) =>
        SaveChoice(id, lineId, choiceId, input, ct);

    [HttpDelete("lines/{lineId:int}/choices/{choiceId:int}")]
    public async Task<IActionResult> DeleteChoice(int id, int lineId, int choiceId, CancellationToken ct)
    {
        var count = await db.Choices.Where(choice => choice.DialogueId == id &&
            choice.SourceLineId == lineId && choice.Id == choiceId).ExecuteDeleteAsync(ct);
        return count == 0 ? NotFound() : NoContent();
    }

    private async Task<IActionResult> SaveChoice(int id, int lineId, int? choiceId, ChoiceInput input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await FindLine(id, lineId, ct);
        if (source is null)
            return NotFound();

        var choice = choiceId is int existingId
            ? await db.Choices.SingleOrDefaultAsync(item => item.DialogueId == id &&
                item.SourceLineId == lineId && item.Id == existingId, ct)
            : new DialogueChoice { DialogueId = id, SourceLineId = lineId, Text = input.Text.Trim() };
        if (choice is null)
            return NotFound();
        if (source.IsEnding)
            return Conflict(new ProblemDetails { Detail = "У концовки не может быть исходящих ответов." });
        if (!await db.Lines.AnyAsync(line => line.DialogueId == id && line.Id == input.TargetLineId, ct))
            return BadRequest(new ProblemDetails { Detail = "Цель перехода должна принадлежать этому диалогу." });
        if (await db.Choices.AnyAsync(item => item.DialogueId == id && item.SourceLineId == lineId &&
            item.Position == input.Position && item.Id != choice.Id, ct))
            return Conflict(new ProblemDetails { Detail = "Эта позиция уже занята другим ответом." });

        choice.TargetLineId = input.TargetLineId;
        choice.Text = input.Text.Trim();
        choice.Position = input.Position;
        if (choiceId is null)
            db.Choices.Add(choice);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return choiceId is null
            ? CreatedAtAction(nameof(DialoguesController.Context), "Dialogues", new { id, lineId }, choice)
            : NoContent();
    }

    private Task<DialogueLine?> FindLine(int id, int lineId, CancellationToken ct) =>
        db.Lines.SingleOrDefaultAsync(line => line.DialogueId == id && line.Id == lineId, ct);

    public sealed record LineInput([Range(1, int.MaxValue)] int SpeakerId,
        [Required, StringLength(4000)] string Text, bool IsEnding, bool IsStoryBranch);
    public sealed record StartInput(int? LineId);
    public sealed record ChoiceInput([Range(1, int.MaxValue)] int TargetLineId,
        [Required, StringLength(500)] string Text, [Range(1, int.MaxValue)] int Position);
}
