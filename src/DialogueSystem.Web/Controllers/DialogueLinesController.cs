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
    [HttpGet("lines/{lineId:int}/choices/{choiceId:int}")]
    public async Task<ActionResult<ChoiceSummary>> GetChoice(int id, int lineId, int choiceId, CancellationToken ct)
    {
        var choice = await editor.FindChoiceAsync(id, lineId, choiceId, ct);
        if (choice is null) return NotFound();
        Response.Headers.ETag = $"\"{choice.Version}\"";
        return Ok(choice);
    }
    [HttpGet("lines/page")]
    public async Task<ActionResult<CatalogPage<LineReference>>> Page(int id,
        [FromQuery, StringLength(CatalogLimits.SearchLength)] string? q,
        [FromQuery, Range(1, int.MaxValue)] int? before, bool branchesOnly, CancellationToken ct)
    {
        var page = await editor.PageAsync(id, q, before, branchesOnly, ct);
        return page is null ? NotFound() : Ok(page);
    }
    [HttpGet("lines")]
    public async Task<ActionResult<List<LineSummary>>> List(int id, CancellationToken ct)
    {
        var lines = await editor.ListAsync(id, ct);
        return lines is null ? NotFound() : Ok(lines);
    }

    [HttpPost("lines")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<LineSummary>> Create(int id, LineInput input, CancellationToken ct)
    {
        var result = await editor.CreateAsync(id, input.ToChange(), ct);
        return result.NewLine is { } line
            ? CreatedAtAction(nameof(DialoguesController.GetLine), "Dialogues", new { id, lineId = line.Id }, line)
            : EditResponse(result);
    }

    [HttpPut("lines/{lineId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(int id, int lineId, LineInput input, CancellationToken ct) =>
        input.Version is null ? MissingVersion() : EditResponse(await editor.UpdateAsync(id, lineId, input.ToChange(), ct));

    [HttpPut("start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetStart(int id, StartInput input, CancellationToken ct) =>
        input.Version is null ? VersionPreconditions.Missing() : EditResponse(await editor.SetStartAsync(id, input.LineId, input.Version, ct));

    [HttpDelete("lines/{lineId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, int lineId, [FromHeader(Name = "If-Match")] string? version, CancellationToken ct)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        return error ?? EditResponse(await editor.DeleteAsync(id, lineId, expected, ct));
    }

    [HttpPost("lines/{lineId:int}/choices")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateChoice(int id, int lineId, ChoiceInput input, CancellationToken ct)
    {
        var result = await editor.SaveChoiceAsync(id, lineId, null, input.ToChange(), ct);
        return result.NewChoice is { } choice
            ? CreatedAtAction(nameof(GetChoice), new { id, lineId, choiceId = choice.Id }, choice)
            : EditResponse(result);
    }

    [HttpPut("lines/{lineId:int}/choices/{choiceId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateChoice(int id, int lineId, int choiceId, ChoiceInput input, CancellationToken ct) =>
        input.Version is null ? MissingVersion() : EditResponse(await editor.SaveChoiceAsync(id, lineId, choiceId, input.ToChange(), ct));

    [HttpDelete("lines/{lineId:int}/choices/{choiceId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteChoice(int id, int lineId, int choiceId,
        [FromHeader(Name = "If-Match")] string? version, CancellationToken ct)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        return error ?? EditResponse(await editor.DeleteChoiceAsync(id, lineId, choiceId, expected, ct));
    }

    [HttpPost("lines/{lineId:int}/choices/{choiceId:int}/move")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MoveChoice(int id, int lineId, int choiceId, MoveChoiceInput input, CancellationToken ct) =>
        EditResponse(await editor.MoveChoiceAsync(id, lineId, choiceId, input.Direction, input.Version, ct));

    public sealed record MoveChoiceInput([Range(-1, 1)] int Direction,
        [Required, StringLength(64, MinimumLength = 64)] string Version);

    private ActionResult EditResponse(EditResult result) => result.Status switch
    {
        EditStatus.Saved => NoContent(),
        EditStatus.NotFound => NotFound(),
        EditStatus.Invalid => BadRequest(new ProblemDetails { Detail = result.Detail }),
        EditStatus.Conflict => Conflict(new ProblemDetails { Detail = result.Detail }),
        EditStatus.StaleVersion => StatusCode(StatusCodes.Status412PreconditionFailed, new ProblemDetails { Detail = result.Detail }),
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

    public sealed record StartInput(int? LineId, [StringLength(64, MinimumLength = 64)] string? Version = null);

    public sealed record ChoiceInput([Range(1, int.MaxValue)] int TargetLineId,
        [Required, StringLength(500)] string Text, [Range(1, int.MaxValue)] int Position,
        [StringLength(DialogueChoice.MaximumStoryStateLength)] string? RequiredState = null,
        [StringLength(DialogueChoice.MaximumStoryStateLength)] string? NextState = null,
        [StringLength(64, MinimumLength = 64)] string? Version = null)
    {
        internal ChoiceChange ToChange() => new(TargetLineId, Text, Position, RequiredState, NextState, Version);
    }
}
