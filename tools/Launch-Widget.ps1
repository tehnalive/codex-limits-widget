$ErrorActionPreference = 'Stop'
$widgetRoot = Split-Path $PSScriptRoot -Parent
$widgetExe = Join-Path $widgetRoot 'Виджет лимитов.exe'
if (-not (Test-Path -LiteralPath $widgetExe)) { $widgetExe = Join-Path $widgetRoot 'artifacts\app\CodexLimitsWidget.exe' }
if (-not (Test-Path -LiteralPath $widgetExe)) { throw 'Сначала запустите tools\Build.ps1.' }
$widgetRuntime = Join-Path $env:LOCALAPPDATA 'CodexLimitsWidget\runtime'
if (-not (Test-Path -LiteralPath (Join-Path $widgetRuntime 'dotnet.exe'))) { $widgetRuntime = Join-Path $env:LOCALAPPDATA 'CodexLimitsWidget\sdk' }
if (Test-Path -LiteralPath (Join-Path $widgetRuntime 'dotnet.exe')) { $env:DOTNET_ROOT = $widgetRuntime }
# This is the visible interactive widget. The Codex helper itself starts without a console.
Start-Process -FilePath $widgetExe -WorkingDirectory $widgetRoot -WindowStyle Normal
