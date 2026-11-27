using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/characters")]
public sealed class CharactersController(ICharacterCatalog catalog) : ControllerBase
{
    [HttpGet]
    public Task<CatalogPage<CharacterSummary>> Page([FromQuery, StringLength(CatalogLimits.SearchLength)] string? q,
        [FromQuery, Range(1, int.MaxValue)] int? before, [FromQuery, Range(1, int.MaxValue)] int? except, CancellationToken ct) =>
        catalog.PageAsync(q, before, except, ct);

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CharacterSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var character = await catalog.FindAsync(id, cancellationToken);
        if (character is null) return NotFound();
        Response.Headers.ETag = $"\"{character.Version}\"";
        return Ok(character);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<CharacterSummary>> Create(CharacterInput input, CancellationToken cancellationToken)
    {
        var character = await catalog.CreateAsync(input.Name, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = character.Id }, character);
    }

    [HttpGet("{id:int}/usage")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CharacterUsage>> Usage(int id, CancellationToken cancellationToken,
        [FromQuery, Range(1, int.MaxValue)] int? before = null)
    {
        var usage = await catalog.UsageAsync(id, cancellationToken, before);
        return usage is null ? NotFound() : Ok(usage);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Update(int id, CharacterInput input, CancellationToken cancellationToken) =>
        input.Version is null ? VersionPreconditions.Missing() :
            VersionPreconditions.Result(await catalog.RenameAsync(id, input.Name, input.Version, cancellationToken));

    [HttpPost("{id:int}/reassign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<ReassignedLines>> Reassign(
        int id, ReassignmentInput input, CancellationToken cancellationToken)
    {
        if (input.Version is null || input.TargetVersion is null) return VersionPreconditions.Missing();
        var result = await catalog.ReassignAsync(id, input.TargetCharacterId, input.DialogueId, input.Version, input.TargetVersion, cancellationToken);
        return result.Status switch
        {
            ReassignmentStatus.Reassigned => Ok(new ReassignedLines(result.UpdatedLineCount)),
            ReassignmentStatus.SameCharacter => BadRequest(new ProblemDetails { Detail = "Выберите другого персонажа." }),
            ReassignmentStatus.SourceNotFound => NotFound(new ProblemDetails { Detail = "Исходный персонаж не найден." }),
            ReassignmentStatus.TargetNotFound => NotFound(new ProblemDetails { Detail = "Новый говорящий не найден." }),
            ReassignmentStatus.DialogueNotFound => NotFound(new ProblemDetails { Detail = "Диалог не найден." }),
            ReassignmentStatus.Conflict => Conflict(new ProblemDetails { Detail = "Персонажи или их реплики уже изменились. Обновите данные перед переносом." }),
            _ => throw new InvalidOperationException()
        };
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Delete(int id, [FromHeader(Name = "If-Match")] string? version, CancellationToken cancellationToken)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        if (error is not null) return error;
        return await catalog.DeleteAsync(id, expected, cancellationToken) switch
        {
            CharacterRemoval.Removed => NoContent(),
            CharacterRemoval.NotFound => NotFound(),
            CharacterRemoval.Conflict => StatusCode(StatusCodes.Status412PreconditionFailed,
                new ProblemDetails { Detail = "Персонаж уже изменился. Обновите данные перед удалением." }),
            CharacterRemoval.InUse => Conflict(new ProblemDetails
            {
                Detail = "Персонаж указан в репликах. Сначала смените говорящего в этих репликах."
            }),
            _ => throw new InvalidOperationException()
        };
    }

    public sealed record CharacterInput([Required, StringLength(120)] string Name, [StringLength(64, MinimumLength = 64)] string? Version = null);

    public sealed record ReassignmentInput(
        [Range(1, int.MaxValue)] int TargetCharacterId,
        [Range(1, int.MaxValue)] int? DialogueId,
        [StringLength(64, MinimumLength = 64)] string? Version = null,
        [StringLength(64, MinimumLength = 64)] string? TargetVersion = null);
}
