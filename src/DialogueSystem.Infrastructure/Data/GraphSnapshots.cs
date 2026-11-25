using DialogueSystem.Application;
using Microsoft.Extensions.Caching.Memory;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class GraphSnapshots : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });

    public bool TryGet(int id, long revision, out DialogueGraph? graph) =>
        cache.TryGetValue((id, revision), out graph);

    public void Set(int id, long revision, DialogueGraph graph, long size) =>
        cache.Set((id, revision), graph, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, size),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

    public void Dispose() => cache.Dispose();
}
