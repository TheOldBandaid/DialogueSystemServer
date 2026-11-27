[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 5189,
    [switch] $InitializeAccounts,
    [ValidateNotNullOrEmpty()]
    [string] $DataDirectory = (Join-Path $PSScriptRoot 'artifacts/demo-data')
)

$ErrorActionPreference = 'Stop'

function Read-DemoPassword([string] $Login) {
    while ($true) {
        $password = Read-Host "Пароль для $Login (от 12 до 512 символов)" -AsSecureString
        try {
            if ($password.Length -ge 12 -and $password.Length -le 512) {
                return [System.Net.NetworkCredential]::new('', $password).Password
            }
        }
        finally {
            $password.Dispose()
        }
        Write-Warning 'Введённый пароль не подходит по длине.'
    }
}

Get-Command dotnet -ErrorAction Stop | Out-Null
$project = Join-Path $PSScriptRoot 'src/DialogueSystem.Web'
$DataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
$address = "http://127.0.0.1:$Port"

$previousAdminPassword = $env:DialogueSystem__AdminPassword
$previousEditorPassword = $env:DialogueSystem__EditorPassword
try {
    $env:DialogueSystem__AdminPassword = $null
    $env:DialogueSystem__EditorPassword = $null
    & dotnet build $project --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать сервер.' }
    $statusOutput = & dotnet run --project $project --no-build --no-launch-profile -- `
        --maintenance=accounts-status "--DialogueSystem:DataDirectory=$dataDirectory" --Logging:LogLevel:Default=None
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить начальные учётные записи.' }
    $status = $statusOutput | Select-Object -Last 1 | ConvertFrom-Json
    if (!$status.hasAdministrator -and $status.adminExists) {
        throw 'Учётная запись admin существует без прав администратора. Требуется локальное восстановление доступа.'
    }
    if (!$status.adminExists -and ($InitializeAccounts -or !$status.hasAdministrator)) {
        $env:DialogueSystem__AdminPassword = Read-DemoPassword -Login 'admin'
    }
    if (!$status.editorExists) {
        $env:DialogueSystem__EditorPassword = Read-DemoPassword -Login 'editor'
    }

    Write-Host "Редактор $address"
    $serverArguments = @(
        '--environment=Development'
        "--urls=$address"
        "--DialogueSystem:DataDirectory=$dataDirectory"
        '--DialogueSystem:SeedDemo=true'
        '--Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command=Warning'
    )
    & dotnet run --project $project --no-build --no-launch-profile -- @serverArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Сервер завершился с кодом $LASTEXITCODE."
    }
}
finally {
    $env:DialogueSystem__AdminPassword = $previousAdminPassword
    $env:DialogueSystem__EditorPassword = $previousEditorPassword
}
