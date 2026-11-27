using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal static class SqliteReadSnapshot
{
    public static async Task<T> ReadAsync<T>(DialogueDbContext db, Func<Task<T>> read, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await read();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var wrapper = await db.Database.UseTransactionAsync(transaction, ct);
            return await read();
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
