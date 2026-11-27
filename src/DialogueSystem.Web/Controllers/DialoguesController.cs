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
    public Task<CatalogPage<DialogueOverview>> Page([FromQuery, StringLength(CatalogLimits.SearchLength)] string? q,
        [FromQuery] bool? hasStart, [FromQuery, Range(1, int.MaxValue)] int? before, CancellationToken ct) =>
        catalog.PageAsync(q, hasStart, before, ct);

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Update(int id, DialogueSettingsInput input, CancellationToken cancellationToken) =>
        input.Version is null ? VersionPreconditions.Missing() :
            VersionPreconditions.Result(await catalog.UpdateAsync(id,
                new(input.Title, input.StartLineId, input.MinimumPlayerLevel, input.Version), cancellationToken));

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Delete(int id, [FromHeader(Name = "If-Match")] string? version, CancellationToken cancellationToken)
    {
        var error = VersionPreconditions.Header(version, out var expected);
        return error ?? VersionPreconditions.Result(await catalog.DeleteAsync(id, expected, cancellationToken), conditional: true);
    }

    [HttpGet("{id:int}/export")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DialogueDocument>> Export(int id, CancellationToken cancellationToken)
    {
        var document = await catalog.ExportAsync(id, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpGet("{id:int}/map")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DialogueMapSnapshot>> Map(int id, CancellationToken ct)
    {
        var map = await catalog.MapAsync(id, ct);
        if (map is null) return NotFound();
        return map.Complete ? Ok(map) : UnprocessableEntity(new ProblemDetails { Detail = "Диалог превышает допустимый размер карты." });
    }

    [HttpPost("import")]
    [RequestSizeLimit(DialogueLimits.MaximumImportBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<DialogueSummary>> Import(DialogueDocument document, CancellationToken cancellationToken)
    {
        var created = await catalog.ImportAsync(document, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPost("{id:int}/copy")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DialogueSummary>> Copy(int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DialogueCopyInput? input, CancellationToken ct)
    {
        var copy = await catalog.CopyAsync(id, input?.CloneCharacters ?? false, ct);
        return copy is null ? NotFound() : CreatedAtAction(nameof(Get), new { id = copy.Id }, copy);
    }

    [HttpGet("{id:int}/validation")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DialogueValidation>> Validation(int id, CancellationToken cancellationToken)
    {
        var validation = await catalog.ValidateAsync(id, cancellationToken);
        return validation is null ? NotFound() : Ok(validation);
    }

    public sealed record DialogueInput([Required, StringLength(160)] string Title);
    public sealed record DialogueSettingsInput([Required, StringLength(160)] string Title,
        [property: JsonRequired] [Range(1, int.MaxValue)] int? StartLineId,
        [property: JsonRequired] [Range(1, int.MaxValue)] int? MinimumPlayerLevel,
        [StringLength(64, MinimumLength = 64)] string? Version = null);
}
