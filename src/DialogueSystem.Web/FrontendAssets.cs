using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;

namespace DialogueSystem.Web;

public sealed class FrontendAssets
{
    public string Prefix { get; }

    public FrontendAssets(IFileProvider provider)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = provider.GetDirectoryContents("/")
            .Where(file => !file.IsDirectory &&
                (file.Name.EndsWith(".js", StringComparison.Ordinal) || file.Name.EndsWith(".css", StringComparison.Ordinal)))
            .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("Не найдены файлы веб-интерфейса.");
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Name));
            using var content = file.CreateReadStream();
            hash.AppendData(SHA256.HashData(content));
        }
        Prefix = "/assets/" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    public string Url(string name) => $"{Prefix}/{name}";
}
