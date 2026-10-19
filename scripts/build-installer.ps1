<#
.SYNOPSIS
Publishes ca.exe self-contained (ADR-0003) and compiles the Inno Setup installer into artifacts\installer.
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
& $dotnet publish (Join-Path $root 'src\ConvertAnything.Cli') -c Release -r win-x64 --self-contained `
    -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Redistributing the .NET runtime and Magick.NET (ImageMagick and the libraries it bundles) requires shipping their
# notices. Every package and runtime pack in ca.deps.json is what the publish folder contains, so collect from each one
# and fail rather than ship a package without its licence.
# The command may print other lines first (the first-run banner on a fresh machine); take only the folder.
$packages = (& $dotnet nuget locals global-packages --list | Select-String '^global-packages:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
$deps = Get-Content (Join-Path $publish 'ca.deps.json') -Raw | ConvertFrom-Json
$licenceNames = 'Notice.txt', 'Copyright.txt', 'LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT'
$notices = [Text.StringBuilder]::new()
[void]$notices.AppendLine('ConvertAnything includes the following third-party software.')
foreach ($library in $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'project' }) {
    $id, $version = $library.Name -replace '^runtimepack\.', '' -split '/'
    $folder = Join-Path $packages "$($id.ToLowerInvariant())\$version"
    $files = @(Get-ChildItem $folder -File | Where-Object { $licenceNames -contains $_.Name })
    if ($files.Count -eq 0) { throw "No licence or notice file for $id $version in $folder" }
    foreach ($file in $files) {
        [void]$notices.AppendLine().AppendLine(('=' * 78)).AppendLine("$id $version - $($file.Name)").AppendLine(('=' * 78))
        [void]$notices.AppendLine((Get-Content $file.FullName -Raw))
    }
}
[IO.File]::WriteAllText((Join-Path $publish 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), [Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt')

& $Iscc /Qp "/DSourceDir=$publish" (Join-Path $root 'installer\ConvertAnything.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter 'ConvertAnything-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host ("{0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
