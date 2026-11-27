using System.Net;
using System.Text.RegularExpressions;
using DialogueSystem.Web;
using Microsoft.Extensions.FileProviders;

namespace DialogueSystem.Tests;

public sealed class FrontendLoadingTests
{
    [Fact]
    public async Task PagesAndAllImportedModulesShareOneVersionAndRevalidateCachedFiles()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        using var client = app.CreateClient();
        var modules = new Queue<Uri>();
        string? prefix = null;
        foreach (var path in new[] { "/", "/characters", "/dialogues/1" })
        {
            var html = await client.GetStringAsync(path);
            var entry = Regex.Match(html, "await import\\(\"(?<url>/assets/[a-f0-9]+/[^\"]+\\.js)\"\\)");
            Assert.True(entry.Success, path);
            var url = entry.Groups["url"].Value;
            prefix ??= url[..url.LastIndexOf('/')];
            Assert.StartsWith(prefix + "/", url);
            Assert.Contains($"href=\"{prefix}/editor.css\"", html);
            Assert.Contains("Не удалось загрузить интерфейс", html);
            modules.Enqueue(new Uri(client.BaseAddress!, url));
        }
        var seen = new HashSet<Uri>();
        while (modules.TryDequeue(out var url))
        {
            if (!seen.Add(url)) continue;
            Assert.StartsWith(prefix + "/", url.AbsolutePath);
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("javascript", response.Content.Headers.ContentType!.MediaType!);
            Assert.True(response.Headers.CacheControl?.NoCache);
            using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var cached = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.True(cached.Headers.CacheControl?.NoCache);
            var source = await response.Content.ReadAsStringAsync();
            foreach (Match import in Regex.Matches(source, "from [\"'](?<url>\\./[^\"']+)[\"']"))
                modules.Enqueue(new Uri(url, import.Groups["url"].Value));
        }
        Assert.Contains(seen, url => url.AbsolutePath.EndsWith("/workspace.js", StringComparison.Ordinal));
        Assert.Contains(seen, url => url.AbsolutePath.EndsWith("/http-client.js", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingAnImportedModuleChangesTheWholeAssetNamespace()
    {
        var directory = Directory.CreateTempSubdirectory("dialogue-assets-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "editor.js"), "import './workspace.js';");
            File.WriteAllText(Path.Combine(directory.FullName, "workspace.js"), "export const api = 1;");
            using var provider = new PhysicalFileProvider(directory.FullName);
            var original = new FrontendAssets(provider);
            Assert.Equal(original.Prefix, new FrontendAssets(provider).Prefix);
            File.WriteAllText(Path.Combine(directory.FullName, "workspace.js"), "export const api = 2;");
            var updated = new FrontendAssets(provider);
            Assert.NotEqual(original.Url("editor.js"), updated.Url("editor.js"));
            Assert.NotEqual(original.Url("workspace.js"), updated.Url("workspace.js"));
        }
        finally { directory.Delete(recursive: true); }
    }
}
