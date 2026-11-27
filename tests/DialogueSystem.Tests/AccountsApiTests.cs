using DialogueSystem.Application;
using DialogueSystem.Domain;
using DialogueSystem.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class AccountsApiTests
{
    [Fact]
    public async Task EnforcesRolesAndCsrfAndRefreshesExistingSession()
    {
        using var data = new DialogueTestData();
        var adminPassword = Guid.NewGuid().ToString("N");
        var editorPassword = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings:
        [
            new("DialogueSystem:AdminPassword", adminPassword),
            new("DialogueSystem:EditorPassword", editorPassword)
        ]);
        using var guest = new DialogueApiClient(app);
        using var editor = new DialogueApiClient(app);
        using var admin = new DialogueApiClient(app);
        const string accounts = "/api/accounts";
        const string profile = accounts + "/me";

        await guest.Expect(Unauthorized, HttpMethod.Get, profile);
        await editor.Login("editor", editorPassword);
        await admin.Login("admin", adminPassword);

        var users = await admin.GetAll<AccountSummary>(accounts);
        var editorId = Assert.Single(users, user => user.Login == "editor").Id;
        var adminId = Assert.Single(users, user => user.Login == "admin").Id;
        var editorRole = $"{accounts}/{editorId}/role";
        var makeAdmin = new { role = "Administrator", version = (await admin.Get<AccountSummary>($"{accounts}/{editorId}")).Version };
        var makeEditor = new { role = "Editor", version = (await admin.Get<AccountSummary>($"{accounts}/{adminId}")).Version };

        await editor.Expect(Forbidden, HttpMethod.Get, accounts);
        await editor.Expect(Forbidden, HttpMethod.Put, editorRole, makeAdmin);
        await admin.Expect(Conflict, HttpMethod.Put, $"{accounts}/{adminId}/role", makeEditor);
        await admin.Expect(BadRequest, HttpMethod.Put, editorRole, makeAdmin, includeToken: false);
        await admin.Expect(NoContent, HttpMethod.Put, editorRole, makeAdmin);
        Assert.Equal("Administrator", (await editor.Get<AccountSummary>(profile)).Role);

        await admin.Expect(NoContent, HttpMethod.Put, editorRole, makeEditor with { version = (await admin.Get<AccountSummary>($"{accounts}/{editorId}")).Version });
        Assert.Equal("Editor", (await editor.Get<AccountSummary>(profile)).Role);
        await editor.Expect(NoContent, HttpMethod.Post, $"{accounts}/logout");
        await editor.Expect(Unauthorized, HttpMethod.Get, profile);
    }

    [Fact]
    public async Task UpdatesExistingDatabaseAndKeepsConfiguredAccountAfterRestart()
    {
        using var data = new DialogueTestData();
        var connection = new SqliteConnectionStringBuilder { DataSource = data.DatabasePath, ForeignKeys = true };
        var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite(connection.ToString()).Options;
        const string title = "Сторожевая башня";
        var password = Guid.NewGuid().ToString("N");
        await using (var db = new DialogueDbContext(options))
        {
            await db.Database.MigrateAsync("20261120110136_InitialDialogueSchema");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Dialogues (Title) VALUES ({title})");
            await db.Database.MigrateAsync("20261122204328_AddStoryState");
            var existing = new Account { Login = "admin", Role = AccountRoles.Administrator, PasswordHash = "" };
            var hash = new PasswordHasher<Account>().HashPassword(existing, password);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Accounts (Login, Role, PasswordHash) VALUES ({existing.Login}, {existing.Role}, {hash})");
        }

        await using (var app = data.Start())
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            var dialogue = await db.Dialogues.SingleAsync();
            Assert.Equal(title, dialogue.Title);
            Assert.Null(dialogue.MinimumPlayerLevel);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            using var admin = new DialogueApiClient(app);
            await admin.Login("admin", password);
            Assert.Single(await admin.GetAll<AccountSummary>("/api/accounts"));
            Assert.Equal(0, (await db.Accounts.SingleAsync()).SessionVersion);
        }

        await using (var app = data.Start(settings: [new("DialogueSystem:AdminPassword", Guid.NewGuid().ToString("N"))]))
        {
            using var admin = new DialogueApiClient(app);
            await admin.Login("admin", password);
            Assert.Equal("Administrator", (await admin.Get<AccountSummary>("/api/accounts/me")).Role);
        }
    }

    [Fact]
    public async Task OnlyAdministratorCanCreateAnAccountWithAUniqueLogin()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings:
        [
            new("DialogueSystem:AdminPassword", password),
            new("DialogueSystem:EditorPassword", password)
        ]);
        using var guest = new DialogueApiClient(app);
        using var editor = new DialogueApiClient(app);
        using var admin = new DialogueApiClient(app);
        await editor.Login("editor", password);
        await admin.Login("admin", password);
        const string accounts = "/api/accounts";
        var input = new { login = "New.Editor", password, role = AccountRoles.Editor };

        await guest.Expect(Unauthorized, HttpMethod.Post, accounts, input);
        await editor.Expect(Forbidden, HttpMethod.Post, accounts, input);
        await admin.Expect(BadRequest, HttpMethod.Post, accounts, input, includeToken: false);
        await admin.Expect(BadRequest, HttpMethod.Post, accounts, input with { password = "short" });
        var created = await admin.Read<AccountSummary>(HttpMethod.Post, accounts, Created, input);
        Assert.Equal("new.editor", created.Login);
        Assert.Equal(created, await admin.Get<AccountSummary>($"{accounts}/{created.Id}"));
        await admin.Expect(Conflict, HttpMethod.Post, accounts, input with { login = "new.editor" });
        await guest.Login(" NEW.EDITOR ", password);
        Assert.Equal(created, await guest.Get<AccountSummary>($"{accounts}/me"));
    }

    [Fact]
    public async Task ChangingPasswordRejectsOlderSessionsAndKeepsNewCredentialsAfterRestart()
    {
        using var data = new DialogueTestData();
        var oldPassword = Guid.NewGuid().ToString("N");
        var newPassword = Guid.NewGuid().ToString("N");
        const string profile = "/api/accounts/me";
        const string change = profile + "/password";
        var input = new { currentPassword = oldPassword, newPassword };

        await using (var app = data.Start(settings: [new("DialogueSystem:EditorPassword", oldPassword)]))
        using (var editor = new DialogueApiClient(app))
        using (var otherSession = new DialogueApiClient(app))
        {
            await editor.Login("editor", oldPassword);
            await otherSession.Login("editor", oldPassword);
            await editor.Expect(BadRequest, HttpMethod.Put, change, input, includeToken: false);
            await editor.Expect(BadRequest, HttpMethod.Put, change, input with { currentPassword = "wrong" });
            await editor.Expect(BadRequest, HttpMethod.Put, change, input with { newPassword = oldPassword });
            Assert.Equal("editor", (await otherSession.Get<AccountSummary>(profile)).Login);

            await editor.Expect(NoContent, HttpMethod.Put, change, input);
            await editor.Expect(Unauthorized, HttpMethod.Get, profile);
            await otherSession.Expect(Unauthorized, HttpMethod.Get, profile);
        }

        await using (var app = data.Start(settings: [new("DialogueSystem:EditorPassword", oldPassword)]))
        using (var editor = new DialogueApiClient(app))
        {
            await editor.Login("editor", oldPassword, Unauthorized);
            await editor.Login("editor", newPassword);
            Assert.Equal(AccountRoles.Editor, (await editor.Get<AccountSummary>(profile)).Role);
        }
    }
}
