using DialogueSystem.Application;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class LoginProtectionTests
{
    [Fact]
    public async Task LimitsAnAccountAcrossDifferentLoginSpellingsAndLeavesOtherAccountsAvailable()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings:
            [new("DialogueSystem:EditorPassword", password), new("DialogueSystem:AdminPassword", password)]);
        using var guest = new DialogueApiClient(app);
        for (var i = 0; i < 10; i++)
            await guest.Login(i % 2 == 0 ? "editor" : " EDITOR ", "wrong", Unauthorized);
        using var reply = await guest.Send(HttpMethod.Post, "/api/accounts/login", new { login = "editor", password });
        Assert.Equal(TooManyRequests, reply.StatusCode);
        Assert.NotNull(reply.Headers.RetryAfter);
        await guest.Login("admin", password);
        Assert.Equal(AccountRoles.Administrator, (await guest.Get<AccountSummary>("/api/accounts/me")).Role);
    }

    [Fact]
    public async Task LimitsAttemptsFromOneAddressEvenWhenTheLoginChanges()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        using var guest = new DialogueApiClient(app);
        for (var i = 0; i < 30; i++) await guest.Login($"unknown-{i}", "wrong", Unauthorized);
        await guest.Login("another-account", "wrong", TooManyRequests);
        await guest.Expect(OK, HttpMethod.Get, "/api/accounts/csrf");
    }
}
