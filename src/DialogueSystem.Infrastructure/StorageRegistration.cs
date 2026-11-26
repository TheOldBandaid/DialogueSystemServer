using DialogueSystem.Application;
using DialogueSystem.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Infrastructure;

public static class StorageRegistration
{
    public static IServiceCollection AddDialogueStorage(this IServiceCollection services, string dataDirectory)
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "dialogues.db"),
            ForeignKeys = true
        };
        return services
            .AddSingleton<GraphSnapshots>()
            .AddDbContext<DialogueDbContext>(options => options.UseSqlite(connection.ToString()))
            .AddScoped<IAccountStore, SqliteAccounts>()
            .AddScoped<IPasswordService, PasswordService>()
            .AddScoped<IDialogueCatalog, SqliteDialogueCatalog>()
            .AddScoped<IDialogueRevisions, SqliteRevisions>()
            .AddScoped<ICharacterCatalog, SqliteCharacterCatalog>()
            .AddScoped<ILineStore, SqliteLines>()
            .AddScoped<IPreviewStore, SqlitePreviews>();
    }

    public static async Task InitializeDialogueDatabaseAsync(
        this IServiceProvider services, bool includeExamples, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<DialogueDbContext>();
        var databasePath = db.Database.GetDbConnection().DataSource;
        if (File.Exists(databasePath) && (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            var backups = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(databasePath)!, "backups"));
            var name = $"dialogues-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
            var destination = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(backups.FullName, name), Pooling = false
            };
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var backup = new SqliteConnection(destination.ToString());
                await backup.OpenAsync(cancellationToken);
                ((SqliteConnection)db.Database.GetDbConnection()).BackupDatabase(backup);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        await db.Database.MigrateAsync(cancellationToken);
        if (includeExamples)
            await DemoDialogue.SeedAsync(db, cancellationToken);
    }
}
