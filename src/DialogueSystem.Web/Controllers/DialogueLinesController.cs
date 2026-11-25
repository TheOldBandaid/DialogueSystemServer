using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}")]
public sealed class DialogueLinesController(LineEditor editor) : ControllerBase
{
    [HttpGet("lines")]
    public async Task<ActionResult<List<LineSummary>>> List(int id, CancellationToken ct)
    {
        var lines = await editor.ListAsync(id, ct);
        return lines is null ? NotFound() : Ok(lines);
    }

    [HttpPost("lines")]
    public async Task<ActionResult<LineSummary>> Create(int id, LineInput input, CancellationToken ct)
    {
        var result = await editor.CreateAsync(id, input.ToChange(), ct);
        return result.NewLine is { } line
            ? CreatedAtAction(nameof(DialoguesController.Context), "Dialogues", new { id, lineId = line.Id }, line)
            : EditResponse(result);
    }

    [HttpPut("lines/{lineId:int}")]
    public async Task<IActionResult> Update(int id, int lineId, LineInput input, CancellationToken ct) =>
        input.Version is null ? MissingVersion() : EditResponse(await editor.UpdateAsync(id, lineId, input.ToChange(), ct));

    [HttpPut("start")]
    public async Task<IActionResult> SetStart(int id, StartInput input, CancellationToken ct) =>
        EditResponse(await editor.SetStartAsync(id, input.LineId, ct));

    [HttpDelete("lines/{lineId:int}")]
    public async Task<IActionResult> Delete(int id, int lineId, CancellationToken ct) =>
        EditResponse(await editor.DeleteAsync(id, lineId, ct));

    [HttpPost("lines/{lineId:int}/choices")]
    public async Task<IActionResult> CreateChoice(int id, int lineId, ChoiceInput input, CancellationToken ct)
    {
        var result = await editor.SaveChoiceAsync(id, lineId, null, input.ToChange(), ct);
        return result.NewChoice is { } choice
            ? CreatedAtAction(nameof(DialoguesController.Context), "Dialogues", new { id, lineId }, choice)
            : EditResponse(result);
    }

    [HttpPut("lines/{lineId:int}/choices/{choiceId:int}")]
    public async Task<IActionResult> UpdateChoice(int id, int lineId, int choiceId, ChoiceInput input, CancellationToken ct) =>
        input.Version is null ? MissingVersion() : EditResponse(await editor.SaveChoiceAsync(id, lineId, choiceId, input.ToChange(), ct));

    [HttpDelete("lines/{lineId:int}/choices/{choiceId:int}")]
    public async Task<IActionResult> DeleteChoice(int id, int lineId, int choiceId, CancellationToken ct) =>
        await editor.DeleteChoiceAsync(id, lineId, choiceId, ct) ? NoContent() : NotFound();

    private ActionResult EditResponse(EditResult result) => result.Status switch
    {
        EditStatus.Saved => NoContent(),
        EditStatus.NotFound => NotFound(),
        EditStatus.Invalid => BadRequest(new ProblemDetails { Detail = result.Detail }),
        EditStatus.Conflict => Conflict(new ProblemDetails { Detail = result.Detail }),
        _ => throw new InvalidOperationException()
    };

    private ObjectResult MissingVersion() => StatusCode(StatusCodes.Status428PreconditionRequired,
        new ProblemDetails { Detail = "Сначала получите текущую версию реплики и передайте поле version при сохранении." });

    public sealed record LineInput([Range(1, int.MaxValue)] int SpeakerId,
        [Required, StringLength(4000)] string Text, bool IsEnding, bool IsStoryBranch,
        [StringLength(64, MinimumLength = 64)] string? Version = null)
    {
        internal LineChange ToChange() => new(SpeakerId, Text, IsEnding, IsStoryBranch, Version);
    }

    public sealed record StartInput(int? LineId);

    public sealed record ChoiceInput([Range(1, int.MaxValue)] int TargetLineId,
        [Required, StringLength(500)] string Text, [Range(1, int.MaxValue)] int Position,
        [StringLength(DialogueChoice.MaximumStoryStateLength)] string? RequiredState = null,
        [StringLength(DialogueChoice.MaximumStoryStateLength)] string? NextState = null,
        [StringLength(64, MinimumLength = 64)] string? Version = null)
    {
        internal ChoiceChange ToChange() => new(TargetLineId, Text, Position, RequiredState, NextState, Version);
    }
}
