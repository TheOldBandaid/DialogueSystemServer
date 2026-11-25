using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DialogueSystem.Web.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController(AccountService accounts, LoginProtection protection) : ControllerBase
{
    [HttpGet("csrf")]
    public IActionResult Csrf(IAntiforgery antiforgery)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }

    [HttpPost("login")]
    [EnableRateLimiting(LoginProtection.Policy)]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<AccountSummary>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        using var lease = protection.Acquire(request.Login);
        if (!lease.IsAcquired)
        {
            await LoginProtection.RejectAsync(HttpContext, lease, cancellationToken);
            return new EmptyResult();
        }
        var session = await accounts.AuthenticateAsync(request.Login, request.Password, cancellationToken);
        if (session is null)
            return Unauthorized();

        await HttpContext.SignInAsync(SessionPrincipal.Create(session));
        return Ok(session.Summary);
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<AccountSummary> Me() => SessionPrincipal.Summary(User);

    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return NoContent();
    }

    [HttpGet]
    [Authorize(Roles = AccountRoles.Administrator)]
    public Task<IReadOnlyList<AccountSummary>> List(CancellationToken cancellationToken) =>
        accounts.ListAsync(cancellationToken);

    [HttpPost]
    [Authorize(Roles = AccountRoles.Administrator)]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<AccountSummary>> Create(
        CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.CreateAsync(request.Login, request.Password, request.Role, cancellationToken);
        if (account is null)
            return Conflict(new ProblemDetails { Detail = "Пользователь с таким именем уже существует." });

        return CreatedAtAction(nameof(Get), new { id = account.Id }, account);
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = AccountRoles.Administrator)]
    public async Task<ActionResult<AccountSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var account = await accounts.GetSessionAsync(id, cancellationToken);
        return account is null ? NotFound() : Ok(account.Summary);
    }

    [HttpPut("{id:int}/role")]
    [Authorize(Roles = AccountRoles.Administrator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(int id, RoleRequest request, CancellationToken cancellationToken) =>
        await accounts.ChangeRoleAsync(id, request.Role, cancellationToken) switch
        {
            RoleChange.Changed => NoContent(),
            RoleChange.NotFound => NotFound(),
            RoleChange.LastAdministrator => Conflict(new ProblemDetails
            {
                Detail = "В системе должен остаться администратор."
            }),
            _ => throw new InvalidOperationException()
        };

    [HttpPut("me/password")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(PasswordRequest request, CancellationToken cancellationToken)
    {
        var change = await accounts.ChangePasswordAsync(
            SessionPrincipal.UserId(User), request.CurrentPassword, request.NewPassword, cancellationToken);
        if (change == PasswordChange.Changed)
        {
            await HttpContext.SignOutAsync();
            return NoContent();
        }

        return change switch
        {
            PasswordChange.WrongPassword => BadRequest(new ProblemDetails { Detail = "Текущий пароль указан неверно." }),
            PasswordChange.SamePassword => BadRequest(new ProblemDetails { Detail = "Укажите новый пароль." }),
            PasswordChange.StaleAccount => Conflict(new ProblemDetails
            {
                Detail = "Пароль уже изменился. Войдите снова."
            }),
            _ => throw new InvalidOperationException()
        };
    }

    public sealed record LoginRequest(
        [Required, MaxLength(100)] string Login,
        [Required, MaxLength(512)] string Password);

    public sealed record CreateAccountRequest(
        [Required, RegularExpression("[A-Za-z0-9][A-Za-z0-9._-]{2,99}")] string Login,
        [Required, StringLength(512, MinimumLength = 12)] string Password,
        [Required, RegularExpression(AccountRoles.Pattern)] string Role);

    public sealed record RoleRequest([Required, RegularExpression(AccountRoles.Pattern)] string Role);

    public sealed record PasswordRequest(
        [Required, MaxLength(512)] string CurrentPassword,
        [Required, StringLength(512, MinimumLength = 12)] string NewPassword);
}
