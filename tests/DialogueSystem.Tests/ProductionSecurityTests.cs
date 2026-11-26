using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DialogueSystem.Tests;

public sealed class ProductionSecurityTests
{
    [Fact]
    public async Task RedirectsHttpAndUsesSecureCookiesAndSanitizedErrors()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var parent = data.Start(Environments.Production, settings: [new("DialogueSystem:EditorPassword", password)]);
        await using var app = parent.WithWebHostBuilder(web => web.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(TestFailureController).Assembly)));
        using var http = app.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        using var redirected = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirected.StatusCode);
        Assert.Equal("https", redirected.Headers.Location!.Scheme);

        using var https = app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var csrf = await https.GetFromJsonAsync<JsonElement>("/api/accounts/csrf");
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/login")
        { Content = JsonContent.Create(new { login = "editor", password }) };
        login.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using var signedIn = await https.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("DialogueSystem.Session=", StringComparison.Ordinal) && value.Contains("secure", StringComparison.OrdinalIgnoreCase));
        using var profile = await https.GetAsync("/api/accounts/me");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);

        using var failure = await https.GetAsync("/api/test/failure");
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        var body = await failure.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TestFailureController.Marker, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        using var details = JsonDocument.Parse(body);
        Assert.Equal(500, details.RootElement.GetProperty("status").GetInt32());
    }
}

[ApiController]
[Route("api/test/failure")]
public sealed class TestFailureController : ControllerBase
{
    public const string Marker = "internal-error-marker";
    [HttpGet]
    public IActionResult Get() => throw new InvalidOperationException(Marker);
}
