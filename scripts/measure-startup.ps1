<#
.SYNOPSIS
  Measures ca.exe process start-to-exit time on the path every Explorer click takes before any work starts.
.DESCRIPTION
  Runs `ca.exe run <unknown-op> <file>`, which parses arguments, writes one log line and exits without
  loading WinForms or opening a pipe. Prints the median of -Runs measurements in milliseconds.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\ca.exe'),
    [int]$Runs = 10
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$times = 1..$Runs | ForEach-Object {
    (Measure-Command { Start-Process -FilePath $Exe -ArgumentList 'run', 'measure.startup', 'C:\nonexistent.txt' -Wait }).TotalMilliseconds
}
$sorted = $times | Sort-Object
$median = ($sorted[[math]::Floor(($Runs - 1) / 2)] + $sorted[[math]::Ceiling(($Runs - 1) / 2)]) / 2
"runs=$Runs median_ms={0:N0} min_ms={1:N0} max_ms={2:N0}" -f $median, $sorted[0], $sorted[-1]
