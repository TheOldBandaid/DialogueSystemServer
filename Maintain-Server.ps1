[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('AccountsStatus', 'ResetAdminPassword', 'Backup', 'Restore')]
    [string] $Action,
    [string] $DataDirectory = (Join-Path $PSScriptRoot 'artifacts/demo-data'),
    [string] $Login,
    [string] $File
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/DialogueSystem.Web'
$directory = [System.IO.Path]::GetFullPath($DataDirectory)
if ($Action -eq 'ResetAdminPassword' -and [string]::IsNullOrWhiteSpace($Login)) {
    throw 'Укажите -Login существующего пользователя, которому нужно восстановить доступ администратора.'
}
if ($Action -eq 'Restore' -and [string]::IsNullOrWhiteSpace($File)) { throw 'Укажите -File резервной копии.' }
& dotnet build $project --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать сервер.' }
$command = switch ($Action) {
    'AccountsStatus' { 'accounts-status' }
    'ResetAdminPassword' { 'reset-admin' }
    'Backup' { 'backup' }
    'Restore' { 'restore' }
}
$arguments = @("--maintenance=$command", "--DialogueSystem:DataDirectory=$directory", '--Logging:LogLevel:Default=None')
if ($Login) { $arguments += "--login=$Login" }
if ($File) { $arguments += "--file=$([System.IO.Path]::GetFullPath($File))" }
& dotnet run --project $project --no-build --no-launch-profile -- @arguments
if ($LASTEXITCODE -ne 0) { throw "Команда обслуживания завершилась с кодом $LASTEXITCODE." }
