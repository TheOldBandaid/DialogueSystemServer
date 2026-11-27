using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web;

internal sealed class ApiAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store";
            var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
            if (context.User.Identity?.IsAuthenticated == true && SessionPrincipal.MustChangePassword(context.User) &&
                path is not ("/api/accounts/me" or "/api/accounts/me/password" or "/api/accounts/logout" or "/api/accounts/csrf"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ApiErrors.WriteAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Detail = "Перед работой необходимо сменить временный пароль.",
                    Extensions = { ["code"] = "password-change-required" }
                });
                return;
            }
        }
        await next(context);
    }
}
