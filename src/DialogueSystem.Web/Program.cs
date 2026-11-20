using System.Security.Claims;
using DialogueSystem.Web.Controllers;
using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Directory.CreateDirectory(
    builder.Configuration["DialogueSystem:DataDirectory"] ??
    Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
var connection = new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(dataDirectory.FullName, "dialogues.db"),
    ForeignKeys = true
};
builder.Services.AddDbContext<DialogueDbContext>(options => options.UseSqlite(connection.ToString()));
builder.Services.AddDataProtection().PersistKeysToFileSystem(dataDirectory.CreateSubdirectory("keys"));
builder.Services.AddScoped<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "DialogueSystem.Session";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Events.OnValidatePrincipal = async context =>
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<DialogueDbContext>();
        if (context.Principal is not { } principal ||
            !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            context.RejectPrincipal();
            return;
        }
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == userId, context.HttpContext.RequestAborted);
        if (account is null)
        {
            context.RejectPrincipal();
            return;
        }

        if (!principal.IsInRole(account.Role))
        {
            context.ReplacePrincipal(AccountsController.CreatePrincipal(account));
            context.ShouldRenew = true;
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddControllersWithViews();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment() && app.Configuration.GetValue("DialogueSystem:SeedDemo", true))
    {
        await DemoDialogue.SeedAsync(db);
    }

    var adminPassword = app.Configuration["DialogueSystem:AdminPassword"];
    var editorPassword = app.Configuration["DialogueSystem:EditorPassword"];
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>();
    await SeedAccount("admin", "Administrator", adminPassword);
    await SeedAccount("editor", "Editor", editorPassword);

    async Task SeedAccount(string login, string role, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await db.Accounts.AnyAsync(account => account.Login == login))
        {
            var account = new Account { Login = login, Role = role, PasswordHash = "" };
            account.PasswordHash = hasher.HashPassword(account, password);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        await transaction.CommitAsync();
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => "Сервер диалогов запущен.");

app.Run();
