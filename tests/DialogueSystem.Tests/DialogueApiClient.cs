using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        HttpMethod method, string path, object? payload = null, bool includeToken = true, bool includeVersion = true, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        var body = payload is null ? null : JsonSerializer.SerializeToNode(payload, JsonSerializerOptions.Web);
        if (includeVersion)
        {
            var parts = path.Split('?')[0].Trim('/').Split('/');
            if (method == HttpMethod.Put && parts.Length == 4 && parts[1] == "accounts" && parts[3] == "role" && body is JsonObject role && !role.ContainsKey("version"))
            {
                using var target = await client.GetAsync($"/api/accounts/{parts[2]}");
                role["version"] = target.IsSuccessStatusCode
                    ? (await target.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<string>() : new string('0', 64);
            }
            var line = parts.Length >= 5 && parts[0] == "api" && parts[1] == "dialogues" && parts[3] == "lines";
            var metadata = parts.Length >= 3 && parts[0] == "api" &&
                (parts[1] == "dialogues" && (parts.Length == 3 || parts.Length == 4 && parts[3] is "start" or "minimum-level") ||
                 parts[1] == "characters" && (parts.Length == 3 || parts.Length == 4 && parts[3] == "reassign"));
            if ((method == HttpMethod.Put || method == HttpMethod.Delete || method == HttpMethod.Post && parts.LastOrDefault() == "reassign") &&
                (line || metadata))
            {
                var input = body as JsonObject ?? new JsonObject();
                if (method == HttpMethod.Delete && ifMatch is null || method != HttpMethod.Delete && !input.ContainsKey("version"))
                {
                    var singleChoice = line && parts.Length == 7;
                    var route = singleChoice ? path : "/" + string.Join('/', parts.Take(line ? 5 : 3)) + (line ? "/context" : "");
                    using var currentReply = await client.GetAsync(route);
                    var token = new string('0', 64);
                    if (currentReply.IsSuccessStatusCode)
                    {
                        var current = (await currentReply.Content.ReadFromJsonAsync<JsonObject>())!;
                        if (singleChoice) token = current["version"]!.GetValue<string>();
                        else if (!line) token = current[method == HttpMethod.Delete && parts[1] == "dialogues" ? "deleteVersion" : "version"]!.GetValue<string>();
                        else if (parts.Length == 5) token = method == HttpMethod.Delete
                            ? current["deleteVersion"]!.GetValue<string>() : current["line"]!["version"]!.GetValue<string>();
                        else token = current["outgoing"]!.AsArray().FirstOrDefault(choice =>
                            choice!["id"]!.GetValue<int>().ToString() == parts[6])?["version"]?.GetValue<string>() ?? token;
                    }
                    if (method == HttpMethod.Delete) ifMatch = token;
                    else input["version"] = token;
                }
                if (parts.LastOrDefault() == "reassign" && !input.ContainsKey("targetVersion"))
                {
                    using var target = await client.GetAsync($"/api/characters/{input["targetCharacterId"]}");
                    input["targetVersion"] = target.IsSuccessStatusCode
                        ? (await target.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<string>() : new string('0', 64);
                }
                if (method != HttpMethod.Delete) body = input;
            }
        }
        if (body is not null) request.Content = JsonContent.Create(body);
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
