using DialogueSystem.Application;
using DialogueSystem.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Tests;

public sealed class AccountInitializationTests
{
    [Fact]
    public async Task ExistingDatabaseWithoutAccountsCanBeInitializedWithoutResettingPasswords()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        using var scope = app.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Assert.True(File.Exists(data.DatabasePath));
        Assert.Equal(new(false, false, false), await accounts.InitialStatusAsync(default));
        var password = Guid.NewGuid().ToString("N");
        Assert.True(await accounts.EnsureAsync("admin", password, AccountRoles.Administrator, default));
        Assert.False(await accounts.EnsureAsync("admin", "ignored", AccountRoles.Administrator, default));
        Assert.NotNull(await accounts.AuthenticateAsync(" ADMIN ", password, default));
        Assert.Equal(new(true, false, true), await accounts.InitialStatusAsync(default));
    }

    [Fact]
    public void DataDirectoryCannotBeOpenedTwiceUntilTheLeaseIsReleased()
    {
        var directory = Directory.CreateTempSubdirectory("dialogue-lock-");
        try
        {
            using (var first = new DataDirectoryLease(directory.FullName))
                Assert.Throws<IOException>(() => new DataDirectoryLease(directory.FullName));
            using var reopened = new DataDirectoryLease(directory.FullName);
        }
        finally { directory.Delete(true); }
    }
}
