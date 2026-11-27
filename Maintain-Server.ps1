[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('AccountsStatus', 'ResetAdminPassword')]
    [string] $Action,
    [string] $DataDirectory = (Join-Path $PSScriptRoot 'artifacts/demo-data'),
    [string] $Login
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/DialogueSystem.Web'
$directory = [System.IO.Path]::GetFullPath($DataDirectory)
if ($Action -eq 'ResetAdminPassword' -and [string]::IsNullOrWhiteSpace($Login)) {
    throw 'Укажите -Login существующего пользователя, которому нужно восстановить доступ администратора.'
}
& dotnet build $project --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать сервер.' }
$command = if ($Action -eq 'AccountsStatus') { 'accounts-status' } else { 'reset-admin' }
$arguments = @("--maintenance=$command", "--DialogueSystem:DataDirectory=$directory", '--Logging:LogLevel:Default=None')
if ($Login) { $arguments += "--login=$Login" }
& dotnet run --project $project --no-build --no-launch-profile -- @arguments
if ($LASTEXITCODE -ne 0) { throw "Команда обслуживания завершилась с кодом $LASTEXITCODE." }
