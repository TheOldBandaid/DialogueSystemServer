using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DialogueSystem.Application;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DialogueSystem.Tests;

internal sealed class DialogueApiClient(WebApplicationFactory<Program> app) : IDisposable
{
    private readonly HttpClient client = app.CreateClient(new() { AllowAutoRedirect = false });
    private string? csrfToken;

    public async Task Login(string login, string password, HttpStatusCode expected = HttpStatusCode.OK)
    {
        csrfToken = (await Get<CsrfResponse>("/api/accounts/csrf")).Token;
        await Expect(expected, HttpMethod.Post, "/api/accounts/login", new { login, password });
        if (expected == HttpStatusCode.OK)
            csrfToken = (await Get<CsrfResponse>("/api/accounts/csrf")).Token;
    }

    public Task<T> Get<T>(string path) => Read<T>(HttpMethod.Get, path);

    public async Task<T[]> GetAll<T>(string path)
    {
        var items = new List<T>();
        int? before = null;
        do
        {
            var route = before is null ? path : $"{path}{(path.Contains('?') ? '&' : '?')}before={before}";
            var page = await Get<CatalogPage<T>>(route);
            items.AddRange(page.Items);
            before = page.NextBeforeId;
        } while (before is not null);
        return items.ToArray();
    }

    public async Task<DialogueSummary[]> GetDialogues(string path)
    {
        var items = await GetAll<DialogueOverview>(path);
        return await Task.WhenAll(items.Select(item => Get<DialogueSummary>($"/api/dialogues/{item.Id}")));
    }

    public async Task<LineSummary[]> GetLines(string path)
    {
        var items = await GetAll<LineReference>(path);
        var route = path.Split('?')[0];
        return await Task.WhenAll(items.Select(item => Get<LineSummary>($"{route}/{item.Id}")));
    }

    public async Task<PreviewSessionSummary[]> GetPreviews(string path)
    {
        var items = new List<PreviewSessionSummary>();
        Guid? before = null;
        do
        {
            var route = before is null ? path : $"{path}?before={before}";
            var page = await Get<PreviewSessionPage>(route);
            items.AddRange(page.Items);
            before = page.NextBeforeId;
        } while (before is not null);
        return items.ToArray();
    }

    public async Task<T> Read<T>(
        HttpMethod method, string path, HttpStatusCode expected = HttpStatusCode.OK, object? payload = null)
    {
        using var response = await Send(method, path, payload);
        AssertStatus(response, expected);
        return await ReadBody<T>(response);
    }

    public async Task Expect(
        HttpStatusCode expected, HttpMethod method, string path, object? payload = null, bool includeToken = true, string? ifMatch = null)
    {
        using var response = await Send(method, path, payload, includeToken, ifMatch);
        AssertStatus(response, expected);
    }

    public async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, object? payload = null, bool includeToken = true, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: JsonSerializerOptions.Web);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

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
