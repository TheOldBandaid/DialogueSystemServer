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
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        await HttpContext.SignInAsync(SessionPrincipal.Create(session, HttpContext.RequestServices.GetRequiredService<ISessionEpoch>()));
        return Ok(session.Summary);
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<AccountSummary> Me() => SessionPrincipal.Summary(User);

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return NoContent();
    }

    [HttpGet]
    [Authorize(Roles = AccountRoles.Administrator)]
    public Task<CatalogPage<AccountSummary>> Page([FromQuery, StringLength(CatalogLimits.SearchLength)] string? q,
        [FromQuery, Range(1, int.MaxValue)] int? before, CancellationToken ct) => accounts.PageAsync(q, before, ct);

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [Authorize(Roles = AccountRoles.Administrator)]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<AccountSummary>> Create(
        CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.CreateAsync(SessionPrincipal.Session(User), request.Login, request.Password, request.Role, cancellationToken);
        return result.Status switch
        {
            AccountCreationStatus.Created => CreatedAtAction(nameof(Get), new { id = result.Account!.Id }, result.Account),
            AccountCreationStatus.DuplicateLogin => Conflict(new ProblemDetails { Detail = "Пользователь с таким именем уже существует." }),
            AccountCreationStatus.Forbidden => Forbid(),
            _ => throw new InvalidOperationException()
        };
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = AccountRoles.Administrator)]
    public async Task<ActionResult<AccountSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var account = await accounts.GetSessionAsync(id, cancellationToken);
        return account is null ? NotFound() : Ok(account.Summary);
    }

    [HttpPut("{id:int}/role")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Authorize(Roles = AccountRoles.Administrator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(int id, RoleRequest request, CancellationToken cancellationToken) =>
        request.Version is null ? VersionPreconditions.Missing() : await accounts.ChangeRoleAsync(SessionPrincipal.Session(User), id, request.Role, request.Version, cancellationToken) switch
        {
            RoleChange.Changed => NoContent(),
            RoleChange.NotFound => NotFound(),
            RoleChange.Forbidden => Forbid(),
            RoleChange.LastAdministrator => Conflict(new ProblemDetails
            {
                Detail = "В системе должен остаться администратор."
            }),
            RoleChange.StaleAccount => Conflict(new ProblemDetails { Detail = "Роль пользователя уже изменилась. Обновите список перед сохранением." }),
            _ => throw new InvalidOperationException()
        };

    [HttpPut("me/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [EnableRateLimiting(LoginProtection.Policy)]
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

    [HttpPost("{id:int}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [EnableRateLimiting(LoginProtection.Policy)]
    [Authorize(Roles = AccountRoles.Administrator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordRequest request, CancellationToken ct) =>
        await accounts.ResetPasswordAsync(SessionPrincipal.UserId(User), id,
            request.CurrentPassword, request.TemporaryPassword, ct) switch
        {
            PasswordReset.Changed => NoContent(),
            PasswordReset.NotFound => NotFound(),
            PasswordReset.Forbidden => Forbid(),
            PasswordReset.WrongPassword => BadRequest(new ProblemDetails { Detail = "Пароль администратора указан неверно." }),
            PasswordReset.OwnAccount => BadRequest(new ProblemDetails { Detail = "Свой пароль изменяйте в разделе «Моя учётная запись»." }),
            PasswordReset.StaleAccount => Conflict(new ProblemDetails { Detail = "Учётная запись уже изменилась. Обновите список." }),
            _ => throw new InvalidOperationException()
        };

    public sealed record ResetPasswordRequest(
        [Required, MaxLength(512)] string CurrentPassword,
        [Required, StringLength(512, MinimumLength = 12)] string TemporaryPassword);

    public sealed record CreateAccountRequest(
        [Required, RegularExpression("[A-Za-z0-9][A-Za-z0-9._-]{2,99}")] string Login,
        [Required, StringLength(512, MinimumLength = 12)] string Password,
        [Required, RegularExpression(AccountRoles.Pattern)] string Role);

    public sealed record RoleRequest([Required, RegularExpression(AccountRoles.Pattern)] string Role,
        [StringLength(64, MinimumLength = 64)] string? Version = null);

    public sealed record PasswordRequest(
        [Required, MaxLength(512)] string CurrentPassword,
        [Required, StringLength(512, MinimumLength = 12)] string NewPassword);
}
