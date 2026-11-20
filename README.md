# Серверная часть системы управления диалогами игровых персонажей

Сервер на C# и ASP.NET Core с SQLite. Пока в проекте только демонстрационный диалог «Ключ от архива».

Требуется .NET 10 SDK. Команды выполняются из корня проекта.

## Запуск сервера

```powershell
dotnet run --project src/DialogueSystem.Web --launch-profile http
```

## Список миграций

```powershell
dotnet tool restore
dotnet ef migrations list --project src/DialogueSystem.Web
```

## Тесты

```powershell
dotnet test DialogueSystem.slnx
```
