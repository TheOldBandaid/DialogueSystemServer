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
[Route("api/dialogues")]
public sealed class DialoguesController(DialogueDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<DialogueSummary>> List(CancellationToken cancellationToken) =>
        await db.Dialogues.OrderBy(dialogue => dialogue.Title)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId))
            .ToListAsync(cancellationToken);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<DialogueSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var dialogue = await db.Dialogues.FindAsync([id], cancellationToken);
        return dialogue is null ? NotFound() : Ok(new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId));
    }

    [HttpPost]
    public async Task<ActionResult<DialogueSummary>> Create(DialogueInput input, CancellationToken cancellationToken)
    {
        var dialogue = new Dialogue { Title = input.Title.Trim() };
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = dialogue.Id },
            new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, DialogueInput input, CancellationToken cancellationToken)
    {
        var dialogue = await db.Dialogues.FindAsync([id], cancellationToken);
        if (dialogue is null)
        {
            return NotFound();
        }

        dialogue.Title = input.Title.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record DialogueInput([Required, StringLength(160)] string Title);

    [HttpGet("{id:int}/lines/{lineId:int}/context")]
    public async Task<ActionResult<LineContext>> Context(int id, int lineId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var graph = await DialogueGraphLoader.LoadAsync(db, id, cancellationToken);
        return graph?.GetContext(lineId) is { } context ? Ok(context) : NotFound();
    }

    [HttpGet("{id:int}/story-branches")]
    public async Task<ActionResult<IReadOnlyList<LineSummary>>> Branches(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var graph = await DialogueGraphLoader.LoadAsync(db, id, cancellationToken);
        return graph is null ? NotFound() : Ok(graph.GetStoryBranches());
    }

    [HttpGet("{id:int}/validation")]
    public async Task<ActionResult<DialogueValidation>> Validation(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var graph = await DialogueGraphLoader.LoadAsync(db, id, cancellationToken);
        return graph is null ? NotFound() : Ok(graph.Validate());
    }
}
