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
    [HttpGet("lines/{lineId:int}")]
    public async Task<ActionResult<LineSummary>> GetLine(int id, int lineId, CancellationToken ct)
    {
        var resource = await editor.ReadAsync(id, lineId, ct);
        if (resource is null) return NotFound();
        Response.Headers.ETag = $"\"{resource.DeleteVersion}\"";
        return Ok(resource.Line);
    }

    [HttpGet("lines/{lineId:int}/context")]
    public async Task<ActionResult<LineContext>> Context(int id, int lineId, CancellationToken cancellationToken,
        [FromQuery, Range(1, int.MaxValue)] int? outgoingAfter = null,
        [FromQuery, Range(1, int.MaxValue)] int? incomingAfterLine = null,
        [FromQuery, Range(1, int.MaxValue)] int? incomingAfterPosition = null,
        [FromQuery, StringLength(64, MinimumLength = 64)] string? version = null)
    {
        if (incomingAfterLine.HasValue != incomingAfterPosition.HasValue)
            return BadRequest(new ProblemDetails { Detail = "Передайте оба поля входящего курсора." });
        if ((outgoingAfter.HasValue || incomingAfterLine.HasValue) && version is null) return VersionPreconditions.Missing();
        var context = await editor.ContextAsync(id, lineId, cancellationToken, outgoingAfter, incomingAfterLine, incomingAfterPosition);
        if (context is not null && version is not null && context.Dialogue!.Version != version)
            return Conflict(new ProblemDetails { Detail = "Связи диалога изменились. Обновите реплику перед загрузкой следующей страницы." });
        return context is null ? NotFound() : Ok(context);
    }

    [HttpGet("lines/{lineId:int}/choices/{choiceId:int}")]
    public async Task<ActionResult<ChoiceSummary>> GetChoice(int id, int lineId, int choiceId, CancellationToken ct)
    {
        var choice = await editor.FindChoiceAsync(id, lineId, choiceId, ct);
        if (choice is null) return NotFound();
        Response.Headers.ETag = $"\"{choice.Version}\"";
        return Ok(choice);
    }
    [HttpGet("lines")]
    public async Task<ActionResult<CatalogPage<LineReference>>> Page(int id,
        [FromQuery, StringLength(CatalogLimits.SearchLength)] string? q,
        [FromQuery, Range(1, int.MaxValue)] int? before, bool branchesOnly, CancellationToken ct)
    {
        var page = await editor.PageAsync(id, q, before, branchesOnly, ct);
        return page is null ? NotFound() : Ok(page);
    }
    [HttpPost("lines")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<LineSummary>> Create(int id, LineInput input, CancellationToken ct)
    {
        var result = await editor.CreateAsync(id, input.ToChange(), ct);
        return result.NewLine is { } line
            ? CreatedAtAction(nameof(GetLine), new { id, lineId = line.Id }, line)
            : VersionPreconditions.Result(result);
    }

    [HttpPut("lines/{lineId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(int id, int lineId, LineInput input, CancellationToken ct) =>
        input.Version is null ? VersionPreconditions.Missing() : VersionPreconditions.Result(await editor.UpdateAsync(id, lineId, input.ToChange(), ct));

    [HttpDelete("lines/{lineId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, int lineId, [FromHeader(Name = "If-Match")] string? version, CancellationToken ct)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        return error ?? VersionPreconditions.Result(await editor.DeleteAsync(id, lineId, expected, ct));
    }

    [HttpPost("lines/{lineId:int}/choices")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateChoice(int id, int lineId, ChoiceInput input, CancellationToken ct)
    {
        var result = await editor.SaveChoiceAsync(id, lineId, null, input.ToChange(), ct);
        return result.NewChoice is { } choice
            ? CreatedAtAction(nameof(GetChoice), new { id, lineId, choiceId = choice.Id }, choice)
            : VersionPreconditions.Result(result);
    }

    [HttpPut("lines/{lineId:int}/choices/{choiceId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateChoice(int id, int lineId, int choiceId, ChoiceInput input, CancellationToken ct) =>
        input.Version is null ? VersionPreconditions.Missing() : VersionPreconditions.Result(await editor.SaveChoiceAsync(id, lineId, choiceId, input.ToChange(), ct));

    [HttpDelete("lines/{lineId:int}/choices/{choiceId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteChoice(int id, int lineId, int choiceId,
        [FromHeader(Name = "If-Match")] string? version, CancellationToken ct)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        return error ?? VersionPreconditions.Result(await editor.DeleteChoiceAsync(id, lineId, choiceId, expected, ct));
    }

    [HttpPost("lines/{lineId:int}/choices/{choiceId:int}/move")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MoveChoice(int id, int lineId, int choiceId, MoveChoiceInput input, CancellationToken ct) =>
        VersionPreconditions.Result(await editor.MoveChoiceAsync(id, lineId, choiceId, input.Direction, input.Version, ct));

    public sealed record MoveChoiceInput([Range(-1, 1)] int Direction,
        [Required, StringLength(64, MinimumLength = 64)] string Version);

    public sealed record LineInput([Range(1, int.MaxValue)] int SpeakerId,
        [Required, StringLength(4000)] string Text, bool IsEnding, bool IsStoryBranch,
        [StringLength(64, MinimumLength = 64)] string? Version = null)
    {
        internal LineChange ToChange() => new(SpeakerId, Text, IsEnding, IsStoryBranch, Version);
    }

    public sealed record ChoiceInput([Range(1, int.MaxValue)] int TargetLineId,
        [Required, StringLength(500)] string Text, [Range(1, int.MaxValue)] int Position,
        [StringLength(DialogueChoice.MaximumStoryStateLength)] string? RequiredState = null,
        [StringLength(DialogueChoice.MaximumStoryStateLength)] string? NextState = null,
        [StringLength(64, MinimumLength = 64)] string? Version = null)
    {
        internal ChoiceChange ToChange() => new(TargetLineId, Text, Position, RequiredState, NextState, Version);
    }
}
