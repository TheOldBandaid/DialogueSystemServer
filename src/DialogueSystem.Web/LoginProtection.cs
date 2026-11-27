using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace DialogueSystem.Web;

public sealed class LoginProtection : IDisposable
{
    public const string Policy = "login";
    private readonly PartitionedRateLimiter<string> accounts = PartitionedRateLimiter.Create<string, string>(login =>
        RateLimitPartition.GetSlidingWindowLimiter(login, _ => Window(10)));

    public RateLimitLease Acquire(string login) => accounts.AttemptAcquire(login.Trim().ToLowerInvariant());

    public static SlidingWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6,
        QueueLimit = 0, AutoReplenishment = true
    };

    public static async Task RejectAsync(HttpContext context, RateLimitLease lease, CancellationToken ct)
    {
        var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ?
            Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Слишком много попыток входа. Подождите минуту и повторите."
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
    }

    public void Dispose() => accounts.Dispose();
}
