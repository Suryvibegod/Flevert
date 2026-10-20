<#
.SYNOPSIS
Writes THIRD-PARTY-NOTICES.txt into a publish folder, and fails rather than let the installer ship a file without its
licence. Called by build-installer.ps1; tests call it on a fake publish folder.

.PARAMETER Publish
The folder dotnet publish wrote, which the installer packs as it is.

.PARAMETER Packages
The NuGet global-packages folder (dotnet nuget locals global-packages --list).
#>
param(
    [Parameter(Mandatory)][string]$Publish,
    [Parameter(Mandatory)][string]$Packages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Redistributing the .NET runtime and Magick.NET (ImageMagick and the libraries it bundles) requires shipping their
# notices. Every package and runtime pack in flevert.deps.json is what the publish folder contains, so collect from each one
# and fail rather than ship a package without its licence.
$deps = Get-Content (Join-Path $Publish 'flevert.deps.json') -Raw | ConvertFrom-Json
$licenceNames = 'Notice.txt', 'Copyright.txt', 'LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT'
$notices = [Text.StringBuilder]::new()
[void]$notices.AppendLine('Flevert includes the following third-party software.')
foreach ($library in $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'project' }) {
    $id, $version = $library.Name -replace '^runtimepack\.', '' -split '/'
    $folder = Join-Path $Packages "$($id.ToLowerInvariant())\$version"
    $files = @(Get-ChildItem $folder -File | Where-Object { $licenceNames -contains $_.Name })
    if ($files.Count -eq 0) { throw "No licence or notice file for $id $version in $folder" }
    foreach ($file in $files) {
        [void]$notices.AppendLine().AppendLine(('=' * 78)).AppendLine("$id $version - $($file.Name)").AppendLine(('=' * 78))
        [void]$notices.AppendLine((Get-Content $file.FullName -Raw))
    }
}
[IO.File]::WriteAllText((Join-Path $Publish 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), [Text.UTF8Encoding]::new($false))
