using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues/{id:int}/revisions")]
public sealed class DialogueRevisionsController(IDialogueRevisions revisions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DialogueRevisionPage>> List(
        int id, [FromQuery, Range(1, int.MaxValue)] int? before, CancellationToken ct)
    {
        var page = await revisions.ListAsync(id, before, ct);
        return page is null ? NotFound() : Ok(page);
    }

    [HttpPost]
    public async Task<ActionResult<DialogueRevisionSummary>> Create(int id, RevisionInput input, CancellationToken ct)
    {
        try
        {
            var created = await revisions.CreateAsync(id, input.Label, User.Identity!.Name!, ct);
            return created is null ? NotFound() : CreatedAtAction(nameof(Export), new { id, revisionId = created.Id }, created);
        }
        catch (DialogueSnapshotConflictException error)
        {
            return Conflict(new ProblemDetails { Detail = error.Message });
        }
    }

    [HttpGet("{revisionId:int}/export")]
    public async Task<ActionResult<DialogueDocument>> Export(int id, int revisionId, CancellationToken ct)
    {
        var document = await revisions.ExportAsync(id, revisionId, ct);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost("{revisionId:int}/copy")]
    public async Task<ActionResult<DialogueSummary>> Copy(int id, int revisionId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DialogueCopyInput? input, CancellationToken ct)
    {
        var copy = await revisions.CopyAsync(id, revisionId, ct, input?.CloneCharacters ?? false);
        return copy is null ? NotFound() :
            CreatedAtAction(nameof(DialoguesController.Get), "Dialogues", new { id = copy.Id }, copy);
    }

    public sealed record RevisionInput([Required, StringLength(80)] string Label);
}
