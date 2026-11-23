using DialogueSystem.Application;
using DialogueSystem.Infrastructure;
using DialogueSystem.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Directory.CreateDirectory(
    builder.Configuration["DialogueSystem:DataDirectory"] ??
    Path.Combine(builder.Environment.ContentRootPath, "App_Data"));

builder.Services.AddDialogueStorage(dataDirectory.FullName);
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<LineEditor>();
builder.Services.AddScoped<PreviewService>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(dataDirectory.CreateSubdirectory("keys"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "DialogueSystem.Session";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Events.OnValidatePrincipal = SessionPrincipal.ValidateAsync;
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.InitializeDialogueDatabaseAsync(app.Environment.IsDevelopment() &&
        app.Configuration.GetValue("DialogueSystem:SeedDemo", true));

    var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
    if (app.Configuration["DialogueSystem:AdminPassword"] is { Length: > 0 } adminPassword)
        await accounts.CreateAsync("admin", adminPassword, AccountRoles.Administrator, CancellationToken.None);
    if (app.Configuration["DialogueSystem:EditorPassword"] is { Length: > 0 } editorPassword)
        await accounts.CreateAsync("editor", editorPassword, AccountRoles.Editor, CancellationToken.None);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

public partial class Program { }
