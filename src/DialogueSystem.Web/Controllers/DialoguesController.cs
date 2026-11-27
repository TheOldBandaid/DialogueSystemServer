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

    [HttpGet("page")]
    public Task<CatalogPage<DialogueOverview>> Page([FromQuery, StringLength(CatalogLimits.SearchLength)] string? q,
        [FromQuery] bool? hasStart, [FromQuery, Range(1, int.MaxValue)] int? before, CancellationToken ct) =>
        catalog.PageAsync(q, hasStart, before, ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<DialogueSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var dialogue = await catalog.FindAsync(id, cancellationToken);
        if (dialogue is null) return NotFound();
        Response.Headers.ETag = $"\"{dialogue.DeleteVersion}\"";
        return Ok(dialogue);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<DialogueSummary>> Create(DialogueInput input, CancellationToken cancellationToken)
    {
        var created = await catalog.CreateAsync(input.Title, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(int id, DialogueInput input, CancellationToken cancellationToken) =>
        input.Version is null ? VersionPreconditions.Missing() :
            VersionPreconditions.Result(await catalog.RenameAsync(id, input.Title, input.Version, cancellationToken));

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, [FromHeader(Name = "If-Match")] string? version, CancellationToken cancellationToken)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        return error ?? VersionPreconditions.Result(await catalog.DeleteAsync(id, expected, cancellationToken), conditional: true);
    }

    [HttpPut("{id:int}/minimum-level")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetMinimumLevel(int id, MinimumLevelInput input, CancellationToken cancellationToken) =>
        input.Version is null ? VersionPreconditions.Missing() :
            VersionPreconditions.Result(await catalog.SetMinimumLevelAsync(id, input.MinimumPlayerLevel, input.Version, cancellationToken));

    [HttpGet("{id:int}/export")]
    public async Task<ActionResult<DialogueDocument>> Export(int id, CancellationToken cancellationToken)
    {
        var document = await catalog.ExportAsync(id, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpGet("{id:int}/map")]
    public async Task<ActionResult<DialogueMapSnapshot>> Map(int id, CancellationToken ct)
    {
        var map = await catalog.MapAsync(id, ct);
        if (map is null) return NotFound();
        return map.Complete ? Ok(map) : UnprocessableEntity(new ProblemDetails { Detail = "Диалог превышает допустимый размер карты." });
    }

    [HttpPost("import")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<DialogueSummary>> Import(DialogueDocument document, CancellationToken cancellationToken)
    {
        var created = await catalog.ImportAsync(document, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:int}/lines/{lineId:int}/context")]
    public async Task<ActionResult<LineContext>> Context(int id, int lineId, CancellationToken cancellationToken,
        [FromQuery, Range(1, int.MaxValue)] int? outgoingAfter = null,
        [FromQuery, Range(1, int.MaxValue)] int? incomingAfterLine = null,
        [FromQuery, Range(1, int.MaxValue)] int? incomingAfterPosition = null,
        [FromQuery, StringLength(64, MinimumLength = 64)] string? version = null)
    {
        if (incomingAfterLine.HasValue != incomingAfterPosition.HasValue)
            return BadRequest(new ProblemDetails { Detail = "Передайте оба поля входящего курсора." });
        if ((outgoingAfter.HasValue || incomingAfterLine.HasValue) && version is null) return VersionPreconditions.Missing();
        var context = await catalog.ContextAsync(id, lineId, cancellationToken, outgoingAfter, incomingAfterLine, incomingAfterPosition);
        if (context is not null && version is not null && context.Dialogue!.Version != version)
            return Conflict(new ProblemDetails { Detail = "Связи диалога изменились. Обновите реплику перед загрузкой следующей страницы." });
        return context is null ? NotFound() : Ok(context);
    }

    [HttpGet("{id:int}/lines/{lineId:int}")]
    public async Task<ActionResult<LineSummary>> GetLine(int id, int lineId, CancellationToken ct)
    {
        var resource = await catalog.FindLineAsync(id, lineId, ct);
        if (resource is null) return NotFound();
        Response.Headers.ETag = $"\"{resource.DeleteVersion}\"";
        return Ok(resource.Line);
    }

    [HttpPost("{id:int}/copy")]
    [ProducesResponseType(StatusCodes.Status201Created)]
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

    public sealed record DialogueInput([Required, StringLength(160)] string Title, [StringLength(64, MinimumLength = 64)] string? Version = null);
    public sealed record MinimumLevelInput([property: JsonRequired] [Range(1, int.MaxValue)] int? MinimumPlayerLevel,
        [StringLength(64, MinimumLength = 64)] string? Version = null);
}
