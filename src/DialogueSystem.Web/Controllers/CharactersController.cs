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
[Route("api/characters")]
public sealed class CharactersController(DialogueDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<Character>> List(CancellationToken cancellationToken) =>
        await db.Characters.AsNoTracking().OrderBy(character => character.Name).ToListAsync(cancellationToken);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Character>> Get(int id, CancellationToken cancellationToken)
    {
        var character = await db.Characters.FindAsync([id], cancellationToken);
        return character is null ? NotFound() : Ok(character);
    }

    [HttpPost]
    public async Task<ActionResult<Character>> Create(CharacterInput input, CancellationToken cancellationToken)
    {
        var character = new Character { Name = input.Name.Trim() };
        db.Characters.Add(character);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = character.Id }, character);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CharacterInput input, CancellationToken cancellationToken)
    {
        var character = await db.Characters.FindAsync([id], cancellationToken);
        if (character is null)
        {
            return NotFound();
        }

        character.Name = input.Name.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record CharacterInput([Required, StringLength(120)] string Name);
}
