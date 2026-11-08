<#
.SYNOPSIS
    Mide CPU, GPU y RAM de ReplayClip mientras juegas (fase 4 del plan de pruebas).

.DESCRIPTION
    Toma una muestra por segundo durante -Seconds segundos y muestra el promedio y el máximo de:
      * CPU del proceso, normalizada al total de núcleos (como el Administrador de tareas).
      * Conjunto de trabajo privado (la columna "Memoria" del Administrador de tareas).
      * Uso de cada motor de GPU por el proceso (3D, VideoEncode, Copy...).
    Usa las clases CIM de rendimiento, cuyos nombres no dependen del idioma de Windows.

.EXAMPLE
    .\Measure-ReplayClip.ps1 -Seconds 120
    .\Measure-ReplayClip.ps1 -ProcessName ReplayClip.SmokeTest -Seconds 60
#>
param(
    [string] $ProcessName = "ReplayClip",
    [int] $Seconds = 60,
    [string] $CsvPath = ""
)

$ErrorActionPreference = "Stop"
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "No se encontró el proceso '$ProcessName'. ¿Está ReplayClip en marcha?" }
$processId = $proc.Id
$cores = [Environment]::ProcessorCount
Write-Host "Midiendo $ProcessName (PID $processId) durante $Seconds s en $cores núcleos lógicos..."

$samples = New-Object System.Collections.Generic.List[object]
$lastCpu = $proc.TotalProcessorTime
$lastTime = [DateTime]::UtcNow
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $proc.Refresh()
    $now = [DateTime]::UtcNow
    $cpu = ($proc.TotalProcessorTime - $lastCpu).TotalMilliseconds / ($now - $lastTime).TotalMilliseconds / $cores * 100
    $lastCpu = $proc.TotalProcessorTime
    $lastTime = $now

    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $processId"
    $engines = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine |
        Where-Object { $_.Name -like "pid_$($processId)_*" }
    $gpu = @{}
    foreach ($engine in $engines) {
        $type = ($engine.Name -split "engtype_")[-1]
        $gpu[$type] = [Math]::Max([double]($gpu[$type] ?? 0), [double]$engine.UtilizationPercentage)
    }
    $samples.Add([pscustomobject]@{
        Segundo        = $i + 1
        CpuPct         = [Math]::Round($cpu, 2)
        MemoriaMB      = [Math]::Round($perf.WorkingSetPrivate / 1MB, 1)
        Gpu3dPct       = [double]($gpu["3D"] ?? 0)
        GpuEncodePct   = [double]($gpu["VideoEncode"] ?? 0)
        GpuCopyPct     = [double]($gpu["Copy"] ?? 0)
        GpuVideoProcPct = [double]($gpu["VideoProcessing"] ?? 0)
    })
}

if ($CsvPath) { $samples | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8 }

$summary = foreach ($column in "CpuPct", "MemoriaMB", "Gpu3dPct", "GpuEncodePct", "GpuCopyPct", "GpuVideoProcPct") {
    $stats = $samples | Measure-Object -Property $column -Average -Maximum
    [pscustomobject]@{ Medida = $column; Promedio = [Math]::Round($stats.Average, 2); Maximo = [Math]::Round($stats.Maximum, 2) }
}
$summary | Format-Table -AutoSize
Write-Host "Objetivos: CPU < 3 %, GPU extra < 5 % (3D + VideoProcessing; VideoEncode es un bloque aparte), Memoria < 200 MB."
