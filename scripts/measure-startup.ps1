<#
.SYNOPSIS
  Measures what every extra selected file costs: a flevert.exe follower starting, handing its path to a running leader and exiting.
.DESCRIPTION
  Starts a headless leader running dev.slow-copy (one second per file), then times -Runs follower processes that join it.
  Files and logs live in a temporary folder that is removed afterwards; the real log is not touched.
  Prints the median, minimum and maximum follower start-to-exit time in milliseconds.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\flevert.exe'),
    [int]$Runs = 10
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("flevert-measure-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Start-Flevert([string]$file) {
    $start = New-Object Diagnostics.ProcessStartInfo $Exe
    $start.Arguments = "run dev.slow-copy `"$file`""
    $start.UseShellExecute = $false
    $start.EnvironmentVariables['FLEVERT_LOG_DIR'] = Join-Path $work 'logs'
    $start.EnvironmentVariables['FLEVERT_HEADLESS'] = '1'
    [Diagnostics.Process]::Start($start)
}

try {
    $files = 0..$Runs | ForEach-Object { $f = Join-Path $work "f$_.txt"; Set-Content -LiteralPath $f -Value $_; $f }
    $leader = Start-Flevert $files[0]
    Start-Sleep -Milliseconds 300 # let the leader create its pipe

    # Start-Process -Wait polls about once a second in Windows PowerShell, which hides the real time; wait on the process.
    $times = $files[1..$Runs] | ForEach-Object {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        (Start-Flevert $_).WaitForExit()
        $timer.Elapsed.TotalMilliseconds
    }

    $leader.WaitForExit()
    $copies = (Get-ChildItem -LiteralPath $work -Filter '* (copy).txt').Count
    $sorted = $times | Sort-Object
    $median = ($sorted[[math]::Floor(($Runs - 1) / 2)] + $sorted[[math]::Ceiling(($Runs - 1) / 2)]) / 2
    "followers=$Runs median_ms={0:N0} min_ms={1:N0} max_ms={2:N0} copies={3}" -f $median, $sorted[0], $sorted[-1], $copies
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
