using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/dialogues")]
public sealed class DialoguesController(IDialogueCatalog catalog) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DialogueSummary>> List(CancellationToken cancellationToken) =>
        catalog.ListAsync(cancellationToken);

    [HttpGet("overview")]
    public Task<IReadOnlyList<DialogueOverview>> Overview(CancellationToken cancellationToken) =>
        catalog.OverviewAsync(cancellationToken);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<DialogueSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var dialogue = await catalog.FindAsync(id, cancellationToken);
        return dialogue is null ? NotFound() : Ok(dialogue);
    }

    [HttpPost]
    public async Task<ActionResult<DialogueSummary>> Create(DialogueInput input, CancellationToken cancellationToken)
    {
        var created = await catalog.CreateAsync(input.Title, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, DialogueInput input, CancellationToken cancellationToken) =>
        await catalog.RenameAsync(id, input.Title, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) =>
        await catalog.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPut("{id:int}/minimum-level")]
    public async Task<IActionResult> SetMinimumLevel(int id, MinimumLevelInput input, CancellationToken cancellationToken) =>
        await catalog.SetMinimumLevelAsync(id, input.MinimumPlayerLevel, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("{id:int}/export")]
    public async Task<ActionResult<DialogueDocument>> Export(int id, CancellationToken cancellationToken)
    {
        var document = await catalog.ExportAsync(id, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost("import")]
    public async Task<ActionResult<DialogueSummary>> Import(DialogueDocument document, CancellationToken cancellationToken)
    {
        var created = await catalog.ImportAsync(document, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:int}/lines/{lineId:int}/context")]
    public async Task<ActionResult<LineContext>> Context(int id, int lineId, CancellationToken cancellationToken)
    {
        var context = await catalog.ContextAsync(id, lineId, cancellationToken);
        return context is null ? NotFound() : Ok(context);
    }

    [HttpPost("{id:int}/copy")]
    public async Task<ActionResult<DialogueSummary>> Copy(int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DialogueCopyInput? input, CancellationToken ct)
    {
        var copy = await catalog.CopyAsync(id, input?.CloneCharacters ?? false, ct);
        return copy is null ? NotFound() : CreatedAtAction(nameof(Get), new { id = copy.Id }, copy);
    }

    [HttpGet("{id:int}/story-branches")]
    public async Task<ActionResult<IReadOnlyList<LineSummary>>> Branches(int id, CancellationToken cancellationToken)
    {
        var branches = await catalog.BranchesAsync(id, cancellationToken);
        return branches is null ? NotFound() : Ok(branches);
    }

    [HttpGet("{id:int}/validation")]
    public async Task<ActionResult<DialogueValidation>> Validation(int id, CancellationToken cancellationToken)
    {
        var validation = await catalog.ValidateAsync(id, cancellationToken);
        return validation is null ? NotFound() : Ok(validation);
    }

    public sealed record DialogueInput([Required, StringLength(160)] string Title);
    public sealed record MinimumLevelInput([property: JsonRequired] [Range(1, int.MaxValue)] int? MinimumPlayerLevel);
}
