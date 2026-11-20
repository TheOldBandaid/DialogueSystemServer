using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController(DialogueDbContext db, IPasswordHasher<Account> hasher) : ControllerBase
{
    [HttpGet("csrf")]
    public IActionResult Csrf(IAntiforgery antiforgery)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<AccountSummary>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var login = request.Login.Trim().ToLowerInvariant();
        var account = await db.Accounts.SingleOrDefaultAsync(item => item.Login == login, cancellationToken);
        if (account is null || hasher.VerifyHashedPassword(account, account.PasswordHash, request.Password)
            == PasswordVerificationResult.Failed)
        {
            return Unauthorized();
        }

        await HttpContext.SignInAsync(CreatePrincipal(account));
        return Ok(new AccountSummary(account.Id, account.Login, account.Role));
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<AccountSummary> Me() => new AccountSummary(
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), User.Identity!.Name!, User.FindFirstValue(ClaimTypes.Role)!);

    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return NoContent();
    }

    [HttpGet]
    [Authorize(Roles = "Administrator")]
    public async Task<IReadOnlyList<AccountSummary>> List(CancellationToken cancellationToken) =>
        await db.Accounts.OrderBy(account => account.Login)
            .Select(account => new AccountSummary(account.Id, account.Login, account.Role))
            .ToListAsync(cancellationToken);

    [HttpPut("{id:int}/role")]
    [Authorize(Roles = "Administrator")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(int id, RoleRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = await db.Accounts.FindAsync([id], cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        if (account.Role == "Administrator" && request.Role == "Editor"
            && !await db.Accounts.AnyAsync(item => item.Id != id && item.Role == "Administrator", cancellationToken))
        {
            return Conflict(new ProblemDetails { Detail = "В системе должен остаться администратор." });
        }

        account.Role = request.Role;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    internal static ClaimsPrincipal CreatePrincipal(Account account) =>
        new(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, account.Id.ToString()), new(ClaimTypes.Name, account.Login),
             new(ClaimTypes.Role, account.Role)], CookieAuthenticationDefaults.AuthenticationScheme));

    public sealed record LoginRequest(
        [Required, MaxLength(100)] string Login,
        [Required, MaxLength(512)] string Password);

    public sealed record AccountSummary(int Id, string Login, string Role);

    public sealed record RoleRequest([Required, RegularExpression("Administrator|Editor")] string Role);
}
