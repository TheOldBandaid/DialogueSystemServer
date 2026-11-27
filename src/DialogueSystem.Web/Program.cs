using DialogueSystem.Application;
using DialogueSystem.Infrastructure;
using DialogueSystem.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Directory.CreateDirectory(
    builder.Configuration["DialogueSystem:DataDirectory"] ??
    Path.Combine(builder.Environment.ContentRootPath, "App_Data"));

builder.Services.AddDialogueStorage(dataDirectory.FullName);
builder.Services.AddSingleton(_ => new DataDirectoryLease(dataDirectory.FullName));
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<LineEditor>();
builder.Services.AddScoped<PreviewService>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(dataDirectory.CreateSubdirectory("keys"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "DialogueSystem.Session";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Events.OnValidatePrincipal = SessionPrincipal.ValidateAsync;
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHttpsRedirection(options => options.HttpsPort =
    builder.Configuration.GetValue<int?>("HTTPS_PORT") ?? 443);
builder.Services.AddSingleton<LoginProtection>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(LoginProtection.Policy, context => RateLimitPartition.GetSlidingWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => LoginProtection.Window(30)));
    options.OnRejected = (context, ct) => new ValueTask(LoginProtection.RejectAsync(context.HttpContext, context.Lease, ct));
});

await using var app = builder.Build();
var dataLease = app.Services.GetRequiredService<DataDirectoryLease>();
app.Lifetime.ApplicationStopped.Register(dataLease.Dispose);
await using (var scope = app.Services.CreateAsyncScope())
{
    var maintenance = app.Configuration["maintenance"];
    if (maintenance is "backup" or "restore")
    {
        Environment.ExitCode = await MaintenanceCommands.RunAsync(maintenance, scope.ServiceProvider, app.Configuration);
        return;
    }
    await scope.ServiceProvider.InitializeDialogueDatabaseAsync(maintenance is null && app.Environment.IsDevelopment() &&
        app.Configuration.GetValue("DialogueSystem:SeedDemo", true));

    if (maintenance is not null)
    {
        Environment.ExitCode = await MaintenanceCommands.RunAsync(maintenance, scope.ServiceProvider, app.Configuration);
        return;
    }

    var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
    if (app.Configuration["DialogueSystem:AdminPassword"] is { Length: > 0 } adminPassword)
        await accounts.EnsureAsync("admin", adminPassword, AccountRoles.Administrator, CancellationToken.None);
    if (app.Configuration["DialogueSystem:EditorPassword"] is { Length: > 0 } editorPassword)
        await accounts.EnsureAsync("editor", editorPassword, AccountRoles.Editor, CancellationToken.None);
    if (app.Configuration.GetValue("DialogueSystem:Backups:Enabled", true))
        await scope.ServiceProvider.GetRequiredService<DatabaseBackups>().EnsureDailyAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
    if (context.User.Identity?.IsAuthenticated == true && SessionPrincipal.MustChangePassword(context.User) &&
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        path is not ("/api/accounts/me" or "/api/accounts/me/password" or "/api/accounts/logout" or "/api/accounts/csrf"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Detail = "Перед работой необходимо сменить временный пароль.",
            Extensions = { ["code"] = "password-change-required" }
        });
        return;
    }
    await next(context);
});
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

public partial class Program { }
