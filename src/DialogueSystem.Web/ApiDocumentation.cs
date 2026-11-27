using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace DialogueSystem.Web;

internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "Dialogue System API";
        document.Info.Description = "Cookie-аутентификация. Сначала GET /api/accounts/csrf, затем POST /api/accounts/login с X-CSRF-TOKEN. " +
            "После входа получите новый CSRF-токен. Он требуется для всех POST, PUT и DELETE. " +
            "Интерактивного Swagger UI нет; описание JSON доступно только в Development.";
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["SessionCookie"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "DialogueSystem.Session",
            Description = "Защищённая cookie, выдаваемая при входе. Передавайте её с последующими запросами."
        };
        document.Components.SecuritySchemes["CsrfToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-CSRF-TOKEN",
            Description = "Поле token из GET /api/accounts/csrf и соответствующая анти-CSRF cookie. После входа обновить token."
        };
        return Task.CompletedTask;
    }
}

internal sealed class ApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var authorization = metadata.OfType<IAuthorizeData>().ToArray();
        var authenticated = authorization.Length != 0;
        var mutation = context.Description.HttpMethod is "POST" or "PUT" or "DELETE";
        var requirement = new OpenApiSecurityRequirement();
        if (authenticated) requirement[new OpenApiSecuritySchemeReference("SessionCookie", context.Document)] = [];
        if (mutation) requirement[new OpenApiSecuritySchemeReference("CsrfToken", context.Document)] = [];
        operation.Security = requirement.Count == 0 ? [] : [requirement];
        operation.Responses ??= new();
        void Response(string code, string description) => operation.Responses.TryAdd(code, new OpenApiResponse { Description = description });
        if (authenticated) Response("401", "Требуется вход; HTTP-перенаправление не выполняется.");
        if (authenticated) Response("403", "Недостаточно прав или необходимо сменить временный пароль.");
        if (mutation) Response("400", "Некорректный ввод или отсутствует/неверен X-CSRF-TOKEN.");
        if (metadata.OfType<EnableRateLimitingAttribute>().Any())
            Response("429", "Превышено число попыток; время повтора указано в Retry-After.");
        if (authorization.Any(item => !string.IsNullOrEmpty(item.Roles)))
            operation.Description = (operation.Description ?? "") + " Требуемые роли: " + string.Join(", ", authorization.Select(item => item.Roles).Where(role => !string.IsNullOrEmpty(role))) + ".";
        var version = operation.Parameters?.FirstOrDefault(parameter => parameter.Name == "If-Match");
        if (version is OpenApiParameter header)
        {
            header.Required = true;
            header.Description = "Один текущий ETag в двойных кавычках. Без wildcard, слабых ETag и списков.";
        }
        return Task.CompletedTask;
    }
}
