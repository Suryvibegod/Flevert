<#
.SYNOPSIS
Publishes flevert.exe self-contained (ADR-0003) and compiles the Inno Setup installer into artifacts\installer.
Needs the .NET 10 SDK and Inno Setup 6 (free: winget install JRSoftware.InnoSetup --scope user).

.PARAMETER Iscc
The Inno Setup compiler to use. Defaults to ISCC on PATH, then the usual install folders. The release workflow passes
the one it has just installed and checked.
#>
param([string]$Iscc)

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
if ($Iscc) {
    if (-not (Test-Path $Iscc -PathType Leaf)) { throw "ISCC not found at $Iscc" }
} else {
    $Iscc = Find-Tool 'ISCC' @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
}

# A stale file in the publish folder would be packed into the installer.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\Flevert.Cli') -c Release -r win-x64 --self-contained `
    -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# THIRD-PARTY-NOTICES.txt, or a failure if the publish folder holds a file without a licence notice.
# The command may print other lines first (the first-run banner on a fresh machine); take only the folder.
$packages = (& $dotnet nuget locals global-packages --list | Select-String '^global-packages:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
& (Join-Path $PSScriptRoot 'write-notices.ps1') -Publish $publish -Packages $packages
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt')

& $Iscc /Qp "/DSourceDir=$publish" (Join-Path $root 'installer\Flevert.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter 'Flevert-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host ("{0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
