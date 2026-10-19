<#
.SYNOPSIS
  Times ca.exe end to end (headless) on 12 MP photos, single files and a batch of ten, as Explorer starts it: one process
  per file. Per scenario: median of -Runs of the wall time, and of the per-file time ca.exe logs (the operation itself).
  Wall minus operation = start-up, batching window and exit. Baseline and results: tasks/perf/todo.md.
.PARAMETER Src
  A folder with photo.heic, photo.jpg and photo.png. scripts/make_bench_photos.py makes synthetic ones (needs Python with
  Pillow, pillow-heif and NumPy); real phone photos work as well.
.EXAMPLE
  powershell -File scripts/measure-convert.ps1 -Src C:\bench -Exe artifacts\publish\ca.exe
#>
param([string]$Exe, [Parameter(Mandatory)][string]$Src, [int]$Runs = 5, [string]$Out)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 has no $PSScriptRoot in parameter defaults.
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\artifacts\publish\ca.exe' }
$Exe = (Resolve-Path $Exe).Path

$scenarios = @(
    @{ Name = 'heic->jpg x1';          Op = 'image.to-jpg';             File = 'photo.heic'; Count = 1 },
    @{ Name = 'png->jpg x1';           Op = 'image.to-jpg';             File = 'photo.png';  Count = 1 },
    @{ Name = 'jpg->webp x1';          Op = 'image.to-webp';            File = 'photo.jpg';  Count = 1 },
    @{ Name = 'jpg compress-balanced'; Op = 'image.compress-balanced';  File = 'photo.jpg';  Count = 1 },
    @{ Name = 'jpg under-1mb';         Op = 'image.compress-under-1mb'; File = 'photo.jpg';  Count = 1 },
    @{ Name = 'jpg strip-metadata';    Op = 'image.strip-metadata';     File = 'photo.jpg';  Count = 1 },
    @{ Name = 'heic->jpg x10';         Op = 'image.to-jpg';             File = 'photo.heic'; Count = 10 }
)

function Median($xs) { $s = @($xs | Sort-Object); if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }

$rows = foreach ($sc in $scenarios) {
    $walls = @(); $ops = @()
    foreach ($r in 1..$Runs) {
        $work = Join-Path ([IO.Path]::GetTempPath()) ("ca-bench-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory "$work\logs" | Out-Null
        $inputs = 1..$sc.Count | ForEach-Object { $f = Join-Path $work ("p$_" + [IO.Path]::GetExtension($sc.File)); Copy-Item (Join-Path $Src $sc.File) $f; $f }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        # Like Explorer: one process per selected file, started back to back.
        $procs = foreach ($f in $inputs) {
            $psi = New-Object Diagnostics.ProcessStartInfo $Exe
            $psi.Arguments = "run $($sc.Op) `"$f`""
            $psi.UseShellExecute = $false
            $psi.EnvironmentVariables['CONVERTANYTHING_LOG_DIR'] = "$work\logs"
            $psi.EnvironmentVariables['CONVERTANYTHING_HEADLESS'] = '1'
            [Diagnostics.Process]::Start($psi)
        }
        $procs | ForEach-Object { $_.WaitForExit() }
        $walls += $timer.Elapsed.TotalMilliseconds
        $lines = Get-Content "$work\logs\*.log"
        if (@($lines | Where-Object { ($_ -split "`t")[4] -eq 'ok' }).Count -ne $sc.Count) { throw "$($sc.Name): not every file converted: $lines" }
        $ops += ($lines | ForEach-Object { [double](($_ -split "`t")[5]) } | Measure-Object -Sum).Sum
        Remove-Item $work -Recurse -Force
    }
    [pscustomobject]@{ Scenario = $sc.Name; WallMs = [int](Median $walls); OpSumMs = [int](Median $ops); OverheadMs = [int]((Median $walls) - (Median $ops)); WallMin = [int]($walls | Measure-Object -Minimum).Minimum; WallMax = [int]($walls | Measure-Object -Maximum).Maximum }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
if ($Out) { $rows | Export-Csv $Out -NoTypeInformation }
