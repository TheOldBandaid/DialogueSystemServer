using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using DialogueSystem.Domain;
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
    public Task<IReadOnlyList<Character>> List(CancellationToken cancellationToken) =>
        catalog.ListAsync(cancellationToken);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Character>> Get(int id, CancellationToken cancellationToken)
    {
        var character = await catalog.FindAsync(id, cancellationToken);
        return character is null ? NotFound() : Ok(character);
    }

    [HttpPost]
    public async Task<ActionResult<Character>> Create(CharacterInput input, CancellationToken cancellationToken)
    {
        var character = await catalog.CreateAsync(input.Name, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = character.Id }, character);
    }

    [HttpGet("{id:int}/usage")]
    public async Task<ActionResult<CharacterUsage>> Usage(int id, CancellationToken cancellationToken)
    {
        var usage = await catalog.UsageAsync(id, cancellationToken);
        return usage is null ? NotFound() : Ok(usage);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CharacterInput input, CancellationToken cancellationToken) =>
        await catalog.RenameAsync(id, input.Name, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:int}/reassign")]
    public async Task<ActionResult<ReassignedLines>> Reassign(
        int id, ReassignmentInput input, CancellationToken cancellationToken)
    {
        var result = await catalog.ReassignAsync(id, input.TargetCharacterId, input.DialogueId, cancellationToken);
        return result.Status switch
        {
            ReassignmentStatus.Reassigned => Ok(new ReassignedLines(result.UpdatedLineCount)),
            ReassignmentStatus.SameCharacter => BadRequest(new ProblemDetails { Detail = "Выберите другого персонажа." }),
            ReassignmentStatus.SourceNotFound => NotFound(new ProblemDetails { Detail = "Исходный персонаж не найден." }),
            ReassignmentStatus.TargetNotFound => NotFound(new ProblemDetails { Detail = "Новый говорящий не найден." }),
            ReassignmentStatus.DialogueNotFound => NotFound(new ProblemDetails { Detail = "Диалог не найден." }),
            _ => throw new InvalidOperationException()
        };
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) =>
        await catalog.DeleteAsync(id, cancellationToken) switch
        {
            CharacterRemoval.Removed => NoContent(),
            CharacterRemoval.NotFound => NotFound(),
            CharacterRemoval.InUse => Conflict(new ProblemDetails
            {
                Detail = "Персонаж указан в репликах. Сначала смените говорящего в этих репликах."
            }),
            _ => throw new InvalidOperationException()
        };

    public sealed record CharacterInput([Required, StringLength(120)] string Name);

    public sealed record ReassignmentInput(
        [Range(1, int.MaxValue)] int TargetCharacterId,
        [Range(1, int.MaxValue)] int? DialogueId);
}
