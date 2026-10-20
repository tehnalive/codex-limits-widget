param([switch]$SelfContained = $true)
$ErrorActionPreference = 'Stop'
$widgetRoot = Split-Path $PSScriptRoot -Parent
$widgetDotnet = Join-Path $env:LOCALAPPDATA 'CodexLimitsWidget\sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $widgetDotnet)) {
    $widgetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $widgetCommand) { throw 'Нужен .NET SDK 10. Установите SDK с сайта Microsoft и повторите сборку.' }
    $widgetDotnet = $widgetCommand.Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$widgetOutput = Join-Path $widgetRoot 'artifacts\app'
$widgetPublishArgs = @('publish', (Join-Path $widgetRoot 'src\Widget.Windows\Widget.Windows.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', $SelfContained.ToString().ToLowerInvariant(), '-o', $widgetOutput)
if ($SelfContained) { $widgetPublishArgs += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=embedded', '-p:DebugSymbols=false') }
& $widgetDotnet @widgetPublishArgs
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
if ($SelfContained) { Copy-Item -LiteralPath (Join-Path $widgetOutput 'CodexLimitsWidget.exe') -Destination (Join-Path $widgetRoot 'Виджет лимитов.exe') -Force }
Write-Output "Готово: $widgetOutput"
