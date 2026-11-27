using DialogueSystem.Application;

namespace DialogueSystem.Infrastructure;

public sealed class SessionEpoch : ISessionEpoch
{
    public string Value { get; }

    public SessionEpoch(string directory)
    {
        var path = Path.Combine(directory, "session-epoch");
        Value = File.Exists(path) ? File.ReadAllText(path).Trim() : Rotate(directory);
        if (!Guid.TryParseExact(Value, "N", out _))
            throw new InvalidDataException("Повреждён маркер сеансов в каталоге данных.");
    }

    public static string Rotate(string directory)
    {
        var value = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, "session-epoch");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return value;
    }
}
