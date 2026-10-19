<#
.SYNOPSIS
Publishes ca.exe self-contained (ADR-0003) and compiles the Inno Setup installer into artifacts\installer.
Needs the .NET 10 SDK and Inno Setup 6 (free: winget install JRSoftware.InnoSetup --scope user).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'

function Find-Tool([string]$Name, [string[]]$Candidates) {
    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $found = $Candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { throw "$Name not found. Install it or run scripts\setup-dev.ps1." }
    return $found
}

$dotnet = Find-Tool 'dotnet' @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")
$iscc = Find-Tool 'ISCC' @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")

# A stale file in the publish folder would be packed into the installer.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\ConvertAnything.Cli') -c Release -r win-x64 --self-contained `
    -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $iscc /Qp "/DSourceDir=$publish" (Join-Path $root 'installer\ConvertAnything.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter 'ConvertAnything-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host ("{0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
