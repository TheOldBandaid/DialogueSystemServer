namespace DialogueSystem.Infrastructure;

public sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream file;

    public DataDirectoryLease(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            file = new FileStream(Path.Combine(directory, "server.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("Каталог данных уже используется. Остановите другой экземпляр сервера перед обслуживанием.", error);
        }
    }

    public void Dispose() => file.Dispose();
}
