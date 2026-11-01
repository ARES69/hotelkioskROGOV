<#
.SYNOPSIS
    Собирает публикацию киоска и локального агента.

.DESCRIPTION
    publish\        — HotelKiosk.UI: сам киоск (WPF + WebView2)
    publish-agent\  — HotelKiosk.LocalAgent: служба Windows с health-check

.EXAMPLE
    .\scripts\publish.ps1

.EXAMPLE
    .\scripts\publish.ps1 -Configuration Debug -SkipAgent
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$UiOutput,
    [string]$AgentOutput,

    [switch]$SkipAgent,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $sourceRoot '..')).Path

if (-not $UiOutput) { $UiOutput = Join-Path $repoRoot 'publish' }
if (-not $AgentOutput) { $AgentOutput = Join-Path $repoRoot 'publish-agent' }

$UiOutput = [IO.Path]::GetFullPath($UiOutput)
$AgentOutput = [IO.Path]::GetFullPath($AgentOutput)

<#
    Публикация копирует appsettings.json из репозитория — то есть шаблон
    с пустыми ключами. На машине, где киоск уже настроен, это молча сотрёт
    боевые логины и пароли. Поэтому: если в целевом конфиге есть значения,
    сначала делаем копию, а без -Force просто отказываемся продолжать.
#>
function Assert-NoLiveCredentials {
    param([Parameter(Mandatory)][string]$AppSettingsPath)

    if (-not (Test-Path $AppSettingsPath)) { return }

    try {
        $cfg = Get-Content $AppSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Warning "Не удалось разобрать $AppSettingsPath — проверьте конфиг вручную."
        return
    }

    $filled = @()
    if ($cfg.LogusApi.Password)    { $filled += 'LogusApi:Password' }
    if ($cfg.AlfaBankQr.Password)  { $filled += 'AlfaBankQr:Password' }
    if ($cfg.Telegram.BotToken)    { $filled += 'Telegram:BotToken' }

    if ($filled.Count -eq 0) { return }

    if (-not $Force) {
        throw @"
В $AppSettingsPath уже есть боевые значения ($($filled -join ', ')).
Публикация заменит файл шаблоном из репозитория.

Скопируйте конфиг отдельно и верните после публикации,
либо повторите с -Force (будет сделана резервная копия).
"@
    }

    $backup = "$AppSettingsPath.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $AppSettingsPath -Destination $backup -Force
    Write-Warning "Боевой конфиг сохранён: $backup"
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Output
    )

    Write-Host "→ $([IO.Path]::GetFileNameWithoutExtension($Project)) → $Output"

    & dotnet publish $Project --configuration $Configuration --output $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с кодом $LASTEXITCODE ($Project)"
    }
}

$sdk = (& dotnet --version).Trim()
Write-Host "SDK: $sdk"
Write-Host "Конфигурация: $Configuration"

Assert-NoLiveCredentials (Join-Path $UiOutput 'appsettings.json')

Invoke-Publish `
    -Project (Join-Path $sourceRoot 'src\HotelKiosk.UI\HotelKiosk.UI.csproj') `
    -Output $UiOutput

if (-not $SkipAgent) {
    Assert-NoLiveCredentials (Join-Path $AgentOutput 'appsettings.json')

    Invoke-Publish `
        -Project (Join-Path $sourceRoot 'src\HotelKiosk.LocalAgent\HotelKiosk.LocalAgent.csproj') `
        -Output $AgentOutput
}

# Проверяем не «код возврата», а то, без чего киоск не запустится.
$checks = @(
    @{ Path = (Join-Path $UiOutput 'HotelKiosk.UI.exe');        What = 'исполняемый файл киоска' },
    @{ Path = (Join-Path $UiOutput 'wwwroot\kiosk.html');       What = 'интерфейс kiosk.html' },
    @{ Path = (Join-Path $UiOutput 'appsettings.json');         What = 'конфиг киоска' }
)

if (-not $SkipAgent) {
    $checks += @{ Path = (Join-Path $AgentOutput 'HotelKiosk.LocalAgent.exe'); What = 'исполняемый файл агента' }
    $checks += @{ Path = (Join-Path $AgentOutput 'appsettings.json');          What = 'конфиг агента' }
}

foreach ($check in $checks) {
    if (-not (Test-Path $check.Path)) {
        throw "После публикации не найден $($check.What): $($check.Path)"
    }
}

Write-Host ''
Write-Host 'Готово.'
Write-Host "  киоск:  $UiOutput"
if (-not $SkipAgent) {
    Write-Host "  агент:  $AgentOutput"
}
Write-Host ''
Write-Host 'Дальше: заполните appsettings.json (см. README, раздел «Запуск»),'
Write-Host 'затем проверьте: publish\HotelKiosk.UI.exe --selftest'
