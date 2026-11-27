using System.Data.Common;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using DialogueSystem.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class AccountMutationConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationRechecksAdministratorRoleAndSessionAfterPasswordHashing(bool changePassword)
    {
        using var data = new DialogueTestData();
        using var gate = new PasswordGate();
        var password = Guid.NewGuid().ToString("N");
        await using var source = data.Start(settings: [new("DialogueSystem:AdminPassword", password)]);
        await using var app = source.WithWebHostBuilder(web => web.ConfigureServices(services =>
        {
            services.RemoveAll<IPasswordService>();
            services.AddScoped<IPasswordService>(_ => new PausedPasswords(gate));
        }));
        using var actor = new DialogueApiClient(app);
        using var other = new DialogueApiClient(app);
        await actor.Login("admin", password);
        await actor.Read<AccountSummary>(HttpMethod.Post, "/api/accounts", Created,
            new { login = "second.admin", password, role = AccountRoles.Administrator });
        await other.Login(changePassword ? "admin" : "second.admin", password);
        var actorId = (await actor.Get<AccountSummary>("/api/accounts/me")).Id;
        var pending = actor.Send(HttpMethod.Post, "/api/accounts",
            new { login = "inflight.admin", password, role = AccountRoles.Administrator });
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (changePassword)
                await other.Expect(NoContent, HttpMethod.Put, "/api/accounts/me/password",
                    new { currentPassword = password, newPassword = Guid.NewGuid().ToString("N") });
            else
                await other.Expect(NoContent, HttpMethod.Put, $"/api/accounts/{actorId}/role",
            new { role = AccountRoles.Editor, version = (await other.Get<AccountSummary>($"/api/accounts/{actorId}")).Version });
        }
        finally { gate.Released.Set(); }
        using var reply = await pending;
        Assert.Equal(Forbidden, reply.StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<DialogueDbContext>().Accounts.AnyAsync(account => account.Login == "inflight.admin"));
    }

    [Fact]
    public async Task RoleChangeRechecksAuthorityAfterTheAuthenticationSnapshot()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        var probe = new AccountReadProbe();
        await using var source = data.Start(settings: [new("DialogueSystem:AdminPassword", password)]);
        await using var app = source.WithWebHostBuilder(web => web.ConfigureServices(services =>
            services.AddDbContext<DialogueDbContext>(options => options.AddInterceptors(probe))));
        using var actor = new DialogueApiClient(app);
        using var other = new DialogueApiClient(app);
        await actor.Login("admin", password);
        await actor.Read<AccountSummary>(HttpMethod.Post, "/api/accounts", Created,
            new { login = "second.admin", password, role = AccountRoles.Administrator });
        var target = await actor.Read<AccountSummary>(HttpMethod.Post, "/api/accounts", Created,
            new { login = "target.editor", password, role = AccountRoles.Editor });
        await other.Login("second.admin", password);
        var actorId = (await actor.Get<AccountSummary>("/api/accounts/me")).Id;
        var actorVersion = (await other.Get<AccountSummary>($"/api/accounts/{actorId}")).Version;
        probe.AfterRead = () => other.Expect(NoContent, HttpMethod.Put, $"/api/accounts/{actorId}/role", new { role = AccountRoles.Editor, version = actorVersion });
        await actor.Expect(Forbidden, HttpMethod.Put, $"/api/accounts/{target.Id}/role",
            new { role = AccountRoles.Administrator, target.Version });
        Assert.Null(probe.AfterRead);
        Assert.Equal(AccountRoles.Editor, (await other.Get<AccountSummary>($"/api/accounts/{target.Id}")).Role);
    }

    private sealed class AccountReadProbe : DbCommandInterceptor
    {
        public Func<Task>? AfterRead { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (AfterRead is { } action && command.CommandText.Contains("FROM \"Accounts\"", StringComparison.Ordinal))
            {
                AfterRead = null;
                await action();
            }
            return result;
        }
    }

    private sealed class PasswordGate : IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Released { get; } = new();
        public void Dispose() => Released.Dispose();
    }

    private sealed class PausedPasswords(PasswordGate gate) : IPasswordService
    {
        private readonly PasswordHasher<Account> hasher = new();

        public string Hash(Account account, string password)
        {
            if (account.Login == "inflight.admin")
            {
                gate.Started.TrySetResult();
                if (!gate.Released.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Не дождались продолжения проверки.");
            }
            return hasher.HashPassword(account, password);
        }

        public bool Verify(Account account, string password) =>
            hasher.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;
    }
}
