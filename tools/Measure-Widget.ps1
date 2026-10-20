param([Parameter(Mandatory=$true)][int]$WidgetProcessId, [int]$Seconds=30)
$ErrorActionPreference = 'Stop'
$widgetRoot = Split-Path $PSScriptRoot -Parent
$widgetExpectedExe = [System.IO.Path]::GetFullPath((Join-Path $widgetRoot 'artifacts\app\CodexLimitsWidget.exe'))
$widgetRootProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$WidgetProcessId"
if ($widgetRootProcess.ExecutablePath -ne $widgetExpectedExe) { throw 'Указанный процесс не является виджетом этого проекта.' }
function Get-WidgetProcessTree {
    $widgetIds = [System.Collections.Generic.HashSet[int]]::new()
    [void]$widgetIds.Add($WidgetProcessId)
    $widgetInventory = Get-CimInstance Win32_Process
    do {
        $widgetAdded = $false
        foreach ($widgetEntry in $widgetInventory) {
            if ($widgetIds.Contains([int]$widgetEntry.ParentProcessId) -and $widgetIds.Add([int]$widgetEntry.ProcessId)) { $widgetAdded = $true }
        }
    } while ($widgetAdded)
    foreach ($widgetTreeId in $widgetIds) { Get-Process -Id $widgetTreeId -ErrorAction SilentlyContinue }
}
$widgetBefore = @(Get-WidgetProcessTree)
$widgetCpuStart = ($widgetBefore | Measure-Object CPU -Sum).Sum
$widgetStartTime = [datetime]::UtcNow
Start-Sleep -Seconds $Seconds
$widgetAfter = @(Get-WidgetProcessTree)
$widgetSampleSeconds = ([datetime]::UtcNow - $widgetStartTime).TotalSeconds
$widgetCpuEnd = ($widgetAfter | Measure-Object CPU -Sum).Sum
$widgetMeasurement = [pscustomobject]@{
    SampleSeconds = [math]::Round($widgetSampleSeconds,1)
    LogicalProcessors = [Environment]::ProcessorCount
    CpuMachinePercent = [math]::Round(100*($widgetCpuEnd-$widgetCpuStart)/($widgetSampleSeconds*[Environment]::ProcessorCount),3)
    PrivateMiB = [math]::Round(($widgetAfter | Measure-Object PrivateMemorySize64 -Sum).Sum/1MB,1)
    WorkingSetMiB = [math]::Round(($widgetAfter | Measure-Object WorkingSet64 -Sum).Sum/1MB,1)
    Processes = @($widgetAfter | ForEach-Object { [pscustomobject]@{ Name=$_.ProcessName; PrivateMiB=[math]::Round($_.PrivateMemorySize64/1MB,1); WorkingSetMiB=[math]::Round($_.WorkingSet64/1MB,1) } })
}
$widgetMeasurement | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $widgetRoot 'artifacts\resource-measurement.json')
$widgetMeasurement | ConvertTo-Json -Depth 4
