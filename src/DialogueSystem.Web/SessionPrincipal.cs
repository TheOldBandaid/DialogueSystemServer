using System.Globalization;
using System.Security.Claims;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DialogueSystem.Web;

internal static class SessionPrincipal
{
    private const string VersionClaim = "dialogue-session-version";

    public static ClaimsPrincipal Create(AccountSession session) => new(new ClaimsIdentity(
        [
            new(ClaimTypes.NameIdentifier, session.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, session.Login),
            new(ClaimTypes.Role, session.Role),
            new(VersionClaim, session.Version.ToString(CultureInfo.InvariantCulture))
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    public static int UserId(ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    public static AccountSummary Summary(ClaimsPrincipal principal) =>
        new(UserId(principal), principal.Identity!.Name!, principal.FindFirstValue(ClaimTypes.Role)!);

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal ||
            !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            context.RejectPrincipal();
            return;
        }

        var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountService>();
        var session = await accounts.GetSessionAsync(id, context.HttpContext.RequestAborted);
        var version = principal.FindFirstValue(VersionClaim) ?? "0";
        if (session is null || version != session.Version.ToString(CultureInfo.InvariantCulture))
        {
            context.RejectPrincipal();
            return;
        }

        if (!principal.IsInRole(session.Role))
        {
            context.ReplacePrincipal(Create(session));
            context.ShouldRenew = true;
        }
    }
}
