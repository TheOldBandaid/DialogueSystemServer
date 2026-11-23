using DialogueSystem.Application;
using Microsoft.EntityFrameworkCore.Storage;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteTransaction(IDbContextTransaction transaction) : IStorageTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
