using System.ComponentModel.DataAnnotations;
using DialogueSystem.Application;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class AccountRecoveryTests
{
    [Fact]
    public async Task TemporaryPasswordRevokesSessionsAndCannotAccessDataBeforeItIsChanged()
    {
        using var data = new DialogueTestData();
        var adminPassword = Guid.NewGuid().ToString("N");
        var oldPassword = Guid.NewGuid().ToString("N");
        var temporary = Guid.NewGuid().ToString("N");
        var permanent = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings:
            [new("DialogueSystem:AdminPassword", adminPassword), new("DialogueSystem:EditorPassword", oldPassword)]);
        using var admin = new DialogueApiClient(app);
        using var oldSession = new DialogueApiClient(app);
        using var editor = new DialogueApiClient(app);
        await admin.Login("admin", adminPassword);
        await oldSession.Login("editor", oldPassword);
        var users = await admin.Get<AccountSummary[]>("/api/accounts");
        var target = Assert.Single(users, user => user.Login == "editor");
        var route = $"/api/accounts/{target.Id}/password";
        var input = new { currentPassword = adminPassword, temporaryPassword = temporary };
        await oldSession.Expect(Forbidden, HttpMethod.Post, route, input);
        await admin.Expect(BadRequest, HttpMethod.Post, route, input, includeToken: false);
        await admin.Expect(BadRequest, HttpMethod.Post, route, input with { currentPassword = "incorrect" });
        await admin.Expect(NoContent, HttpMethod.Post, route, input);
        await oldSession.Expect(Unauthorized, HttpMethod.Get, "/api/accounts/me");
        await editor.Login("editor", temporary);
        Assert.True((await editor.Get<AccountSummary>("/api/accounts/me")).MustChangePassword);
        await editor.Expect(Forbidden, HttpMethod.Get, "/api/dialogues");
        await editor.Expect(Forbidden, HttpMethod.Post, "/api/dialogues", new { title = "Недоступно" });
        await editor.Expect(NoContent, HttpMethod.Put, "/api/accounts/me/password",
            new { currentPassword = temporary, newPassword = permanent });
        await editor.Expect(Unauthorized, HttpMethod.Get, "/api/accounts/me");
        await editor.Login("editor", temporary, Unauthorized);
        await editor.Login("editor", permanent);
        Assert.False((await editor.Get<AccountSummary>("/api/accounts/me")).MustChangePassword);
        await editor.Expect(OK, HttpMethod.Get, "/api/dialogues");
    }

    [Fact]
    public async Task LocalRecoveryRestoresAdministratorAndInvalidatesOldCookie()
    {
        using var data = new DialogueTestData();
        var oldPassword = Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", oldPassword)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", oldPassword);
        using (var scope = app.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AccountService>();
            Assert.False(await service.RecoverAdministratorAsync("absent", password, default));
            Assert.True(await service.RecoverAdministratorAsync("editor", password, default));
        }
        await client.Expect(Unauthorized, HttpMethod.Get, "/api/accounts/me");
        await client.Login("editor", password);
        Assert.Equal(AccountRoles.Administrator, (await client.Get<AccountSummary>("/api/accounts/me")).Role);
        await client.Expect(OK, HttpMethod.Get, "/api/accounts");
    }

    [Fact]
    public async Task RejectedLocalPasswordLeavesThePasswordRoleAndSessionUnchanged()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var before = await client.Get<AccountSummary>("/api/accounts/me");
        using (var scope = app.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            await Assert.ThrowsAsync<ValidationException>(() => accounts.RecoverAdministratorAsync("editor", new string(' ', 12), default));
            Assert.NotNull(await accounts.AuthenticateAsync("editor", password, default));
        }
        Assert.Equal(before, await client.Get<AccountSummary>("/api/accounts/me"));
        await client.Login("editor", password);
    }
}
