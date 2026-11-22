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

    [HttpGet("{id:int}/export")]
    public async Task<ActionResult<DialogueDocument>> Export(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var graph = await DialogueGraphLoader.LoadAsync(db, id, cancellationToken);
        return graph is null ? NotFound() : Ok(graph.ToDocument());
    }

    [HttpPost("import")]
    public async Task<ActionResult<DialogueSummary>> Import(DialogueDocument document, CancellationToken cancellationToken)
    {
        var created = await DialogueImporter.SaveAsync(db, document, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    public sealed record DialogueInput([Required, StringLength(160)] string Title);

    [HttpGet("{id:int}/lines/{lineId:int}/context")]
    public async Task<ActionResult<LineContext>> Context(int id, int lineId, CancellationToken cancellationToken)
    {
        var neighbors =
            from choice in db.Choices
            where choice.DialogueId == id && (choice.SourceLineId == lineId || choice.TargetLineId == lineId)
            let neighborId = choice.SourceLineId == lineId ? choice.TargetLineId : choice.SourceLineId
            join neighbor in db.Lines
                on new { choice.DialogueId, Id = neighborId } equals new { neighbor.DialogueId, neighbor.Id }
            select new
            {
                choice.DialogueId,
                choice.SourceLineId,
                choice.TargetLineId,
                Link = new ChoiceLink(choice.Id, choice.Text, choice.Position,
                    new LineSummary(neighbor.Id, neighbor.Speaker.Name, neighbor.Text,
                        neighbor.IsEnding, neighbor.IsStoryBranch, neighbor.SpeakerId))
            };

        var context = await db.Lines.AsSingleQuery()
            .Where(line => line.DialogueId == id && line.Id == lineId)
            .Select(line => new
            {
                Line = new LineSummary(line.Id, line.Speaker.Name, line.Text,
                    line.IsEnding, line.IsStoryBranch, line.SpeakerId),
                Neighbors = neighbors.Where(item => item.DialogueId == line.DialogueId).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var incoming = context.Neighbors.Where(item => item.TargetLineId == lineId)
            .OrderBy(item => item.SourceLineId).ThenBy(item => item.Link.Position)
            .Select(item => item.Link).ToArray();
        var outgoing = context.Neighbors.Where(item => item.SourceLineId == lineId)
            .OrderBy(item => item.Link.Position).Select(item => item.Link).ToArray();
        return Ok(new LineContext(context.Line, incoming, outgoing));
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
