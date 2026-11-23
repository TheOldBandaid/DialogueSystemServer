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

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CharacterInput input, CancellationToken cancellationToken) =>
        await catalog.RenameAsync(id, input.Name, cancellationToken) ? NoContent() : NotFound();

    public sealed record CharacterInput([Required, StringLength(120)] string Name);
}
