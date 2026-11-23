namespace DialogueSystem.Application;

public interface IStorageTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
