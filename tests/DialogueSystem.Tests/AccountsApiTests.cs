using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DialogueSystem.Web.Controllers.AccountsController;
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

        var users = await admin.Get<AccountSummary[]>(accounts);
        var editorId = Assert.Single(users, user => user.Login == "editor").Id;
        var adminId = Assert.Single(users, user => user.Login == "admin").Id;
        var editorRole = $"{accounts}/{editorId}/role";
        var makeAdmin = new { role = "Administrator" };
        var makeEditor = new { role = "Editor" };

        await editor.Expect(Forbidden, HttpMethod.Get, accounts);
        await editor.Expect(Forbidden, HttpMethod.Put, editorRole, makeAdmin);
        await admin.Expect(Conflict, HttpMethod.Put, $"{accounts}/{adminId}/role", makeEditor);
        await admin.Expect(BadRequest, HttpMethod.Put, editorRole, makeAdmin, includeToken: false);
        await admin.Expect(NoContent, HttpMethod.Put, editorRole, makeAdmin);
        Assert.Equal("Administrator", (await editor.Get<AccountSummary>(profile)).Role);

        await admin.Expect(NoContent, HttpMethod.Put, editorRole, makeEditor);
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
        await using (var db = new DialogueDbContext(options))
        {
            await db.Database.MigrateAsync("20261120110136_InitialDialogueSchema");
            db.Dialogues.Add(new Dialogue { Title = title });
            await db.SaveChangesAsync();
        }

        var password = Guid.NewGuid().ToString("N");
        await using (var app = data.Start(settings: [new("DialogueSystem:AdminPassword", password)]))
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            Assert.Equal(title, (await db.Dialogues.SingleAsync()).Title);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            using var admin = new DialogueApiClient(app);
            await admin.Login("admin", password);
            Assert.Single(await admin.Get<AccountSummary[]>("/api/accounts"));
        }

        await using (var app = data.Start(settings: [new("DialogueSystem:AdminPassword", Guid.NewGuid().ToString("N"))]))
        {
            using var admin = new DialogueApiClient(app);
            await admin.Login("admin", password);
            Assert.Equal("Administrator", (await admin.Get<AccountSummary>("/api/accounts/me")).Role);
        }
    }
}
