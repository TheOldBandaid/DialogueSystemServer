using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics;

namespace DialogueSystem.Web;

internal static class ApiErrors
{
    public static Task WriteAsync(StatusCodeContext context)
    {
        var http = context.HttpContext;
        if (!http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        return WriteAsync(http, new ProblemDetails { Status = http.Response.StatusCode });
    }

    public static async Task WriteAsync(HttpContext http, ProblemDetails problem)
    {
        var written = await http.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new()
        {
            HttpContext = http,
            ProblemDetails = problem
        });
        if (!written)
            await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: http.RequestAborted);
    }
}
