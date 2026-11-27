using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteSearch : DbConnectionInterceptor
{
    // Executed only by SQLite, through the EF function mapping. No user text becomes SQL.
    public static bool Contains(string value, string query) => throw new NotSupportedException();

    private static void Register(DbConnection connection) => ((SqliteConnection)connection)
        .CreateFunction<string, string, bool>("dialogue_contains",
            (value, query) => value.Contains(query, StringComparison.OrdinalIgnoreCase), isDeterministic: true);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Register(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Register(connection);
        return Task.CompletedTask;
    }
}
