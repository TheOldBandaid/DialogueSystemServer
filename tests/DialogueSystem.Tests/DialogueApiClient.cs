using System.Net;
using System.Net.Http.Json;
using DialogueSystem.Web.Data;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DialogueSystem.Tests;

internal sealed class DialogueApiClient(WebApplicationFactory<DialogueDbContext> app) : IDisposable
{
    private readonly HttpClient client = app.CreateClient(new() { AllowAutoRedirect = false });
    private string? csrfToken;

    public async Task Login(string login, string password)
    {
        csrfToken = (await Get<CsrfResponse>("/api/accounts/csrf")).Token;
        await Expect(HttpStatusCode.OK, HttpMethod.Post, "/api/accounts/login", new { login, password });
        csrfToken = (await Get<CsrfResponse>("/api/accounts/csrf")).Token;
    }

    public Task<T> Get<T>(string path) => Read<T>(HttpMethod.Get, path);

    public async Task<T> Read<T>(
        HttpMethod method, string path, HttpStatusCode expected = HttpStatusCode.OK, object? payload = null)
    {
        using var response = await Send(method, path, payload);
        AssertStatus(response, expected);
        return await ReadBody<T>(response);
    }

    public async Task Expect(
        HttpStatusCode expected, HttpMethod method, string path, object? payload = null, bool includeToken = true)
    {
        using var response = await Send(method, path, payload, includeToken);
        AssertStatus(response, expected);
    }

    public async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, object? payload = null, bool includeToken = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (includeToken && csrfToken is not null && method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        return await client.SendAsync(request);
    }

    public static async Task<T> ReadBody<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>()
        ?? throw new InvalidOperationException("Ответ API не содержит ожидаемые данные.");

    public void Dispose() => client.Dispose();

    private static void AssertStatus(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: " +
            $"ожидался {(int)expected}, получен {(int)response.StatusCode}.");

    private sealed record CsrfResponse(string Token);
}
