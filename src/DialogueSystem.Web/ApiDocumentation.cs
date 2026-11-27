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
        var route = context.Description.RelativePath ?? "";
        if (route.Contains("{id}", StringComparison.Ordinal) && !(route.EndsWith("/previews", StringComparison.Ordinal) && !mutation))
            Response("404", "Объект не найден в указанном родительском ресурсе или недоступен этому пользователю.");
        if (metadata.OfType<EnableRateLimitingAttribute>().Any()) Response("429", "Превышено число попыток; время повтора указано в Retry-After.");
        if (route.EndsWith("/login", StringComparison.Ordinal)) Response("401", "Неверные учётные данные.");
        var bodyVersion = context.Description.HttpMethod == "PUT" && !route.EndsWith("/me/password", StringComparison.Ordinal) ||
            route.EndsWith("/reassign", StringComparison.Ordinal) || route.EndsWith("/cleanup", StringComparison.Ordinal) && mutation ||
            route.EndsWith("/context", StringComparison.Ordinal);
        if (bodyVersion)
        {
            Response("409", "Версия в теле запроса или курсор устарели. Перечитайте данные перед повтором.");
            Response("428", "Передайте обязательную версию или токен подтверждения действия.");
        }
        if (mutation && (route.Contains("/previews", StringComparison.Ordinal) || route.EndsWith("/move", StringComparison.Ordinal) ||
            route.EndsWith("/revisions", StringComparison.Ordinal) || route.Contains("/choices", StringComparison.Ordinal) ||
            route is "api/accounts" or "api/accounts/me/password" || route.EndsWith("/password", StringComparison.Ordinal)))
            Response("409", "Конфликт текущего состояния: проверьте detail и перечитайте данные.");
        if (route.EndsWith("/map", StringComparison.Ordinal) || mutation && route.Contains("/previews", StringComparison.Ordinal))
            Response("422", "Граф или условия начала прохождения не позволяют выполнить действие.");
        if (authorization.Any(item => !string.IsNullOrEmpty(item.Roles)))
            operation.Description = (operation.Description ?? "") + " Требуемые роли: " + string.Join(", ", authorization.Select(item => item.Roles).Where(role => !string.IsNullOrEmpty(role))) + ".";
        if (context.Description.HttpMethod == "DELETE")
        {
            Response("412", "If-Match устарел. Получите данные заново и подтвердите удаление.");
            Response("428", "Передайте If-Match с текущей версией объекта.");
            Response("409", "Объект используется: сначала измените связанные данные.");
            var version = operation.Parameters?.FirstOrDefault(parameter => parameter.Name == "If-Match");
            if (version is OpenApiParameter header) { header.Required = true; header.Description = "Один текущий ETag в двойных кавычках. Без wildcard, слабых ETag и списков."; }
        }
        return Task.CompletedTask;
    }
}
