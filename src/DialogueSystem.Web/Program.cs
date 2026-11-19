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

app.MapGet("/", () => "Сервер диалогов запущен.");

app.Run();
