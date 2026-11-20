using DialogueSystem.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Directory.CreateDirectory(
    builder.Configuration["DialogueSystem:DataDirectory"] ??
    Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
var connection = new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(dataDirectory.FullName, "dialogues.db"),
    ForeignKeys = true
};
builder.Services.AddDbContext<DialogueDbContext>(options => options.UseSqlite(connection.ToString()));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
    await db.Database.MigrateAsync();

    if (app.Environment.IsDevelopment() && app.Configuration.GetValue("DialogueSystem:SeedDemo", true))
    {
        await DemoDialogue.SeedAsync(db);
    }
}

app.MapGet("/", () => "Сервер диалогов запущен.");

app.Run();
