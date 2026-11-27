using DialogueSystem.Application;
using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web;

internal static class VersionPreconditions
{
    public static ObjectResult Missing() => new(new ProblemDetails
    {
        Status = StatusCodes.Status428PreconditionRequired,
        Detail = "Сначала получите текущую версию объекта и передайте version; для удаления используйте If-Match."
    }) { StatusCode = StatusCodes.Status428PreconditionRequired };

    public static ObjectResult? Header(string? header, out string version)
    {
        version = header?.Trim() ?? "";
        if (version.Length == 66 && version.StartsWith('"') && version.EndsWith('"')) version = version[1..^1];
        if (header is null) return Missing();
        if (version.Length != 64 || version.Any(character => !char.IsAsciiHexDigit(character)))
            return new BadRequestObjectResult(new ProblemDetails { Detail = "If-Match должен содержать одну текущую версию объекта." });
        return null;
    }

    public static IActionResult Result(ChangeStatus status, bool conditional = false) => status switch
    {
        ChangeStatus.Saved => new NoContentResult(),
        ChangeStatus.NotFound => new NotFoundResult(),
        ChangeStatus.Conflict => new ObjectResult(new ProblemDetails
            { Detail = "Объект или связанные данные уже изменились. Обновите страницу перед повторным действием." })
            { StatusCode = conditional ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict },
        _ => throw new InvalidOperationException()
    };

    public static ActionResult Result(EditResult result) => result.Status switch
    {
        EditStatus.Saved => new NoContentResult(),
        EditStatus.NotFound => new NotFoundResult(),
        EditStatus.Invalid => new BadRequestObjectResult(new ProblemDetails { Detail = result.Detail }),
        EditStatus.Conflict => new ConflictObjectResult(new ProblemDetails { Detail = result.Detail }),
        EditStatus.StaleVersion => new ObjectResult(new ProblemDetails { Detail = result.Detail })
            { StatusCode = StatusCodes.Status412PreconditionFailed },
        _ => throw new InvalidOperationException()
    };
}
