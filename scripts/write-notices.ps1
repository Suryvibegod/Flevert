<#
.SYNOPSIS
Writes THIRD-PARTY-NOTICES.txt into a publish folder, and fails rather than let the installer ship a file without its
licence. Called by build-installer.ps1; tests call it on a fake publish folder.

.PARAMETER Publish
The folder dotnet publish wrote, which the installer packs as it is.

.PARAMETER Packages
The NuGet global-packages folder (dotnet nuget locals global-packages --list).

.PARAMETER Manifest
Lists, under "notices", the files that come from elsewhere than NuGet and their licences; defaults to scripts\deps.json.
assets\notices.json under Root, when it exists, lists the same way the third-party assets committed in the repository
(the menu icons).

.PARAMETER Root
The folder the licence paths in the manifest are relative to; defaults to the repository root.
#>
param(
    [Parameter(Mandatory)][string]$Publish,
    [Parameter(Mandatory)][string]$Packages,
    [string]$Manifest,
    [string]$Root
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Not as parameter defaults: Windows PowerShell leaves $PSScriptRoot empty there when this script runs with -File.
if (-not $Manifest) { $Manifest = Join-Path $PSScriptRoot 'deps.json' }
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

# Redistributing the .NET runtime and Magick.NET (ImageMagick and the libraries it bundles) requires shipping their
# notices. Every package and runtime pack in flevert.deps.json is what the publish folder contains, so collect from each one
# and fail rather than ship a package without its licence.
$publishRoot = (Resolve-Path -LiteralPath $Publish).Path.TrimEnd('\')
$deps = [IO.File]::ReadAllText((Join-Path $publishRoot 'flevert.deps.json')) | ConvertFrom-Json
$licenceNames = 'Notice.txt', 'Copyright.txt', 'LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT'
$notices = [Text.StringBuilder]::new()
[void]$notices.AppendLine('Flevert includes the following third-party software.')
foreach ($library in $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'project' }) {
    $id, $version = $library.Name -replace '^runtimepack\.', '' -split '/'
    $folder = Join-Path $Packages "$($id.ToLowerInvariant())\$version"
    $files = @(Get-ChildItem -LiteralPath $folder -File -Force | Where-Object { $licenceNames -contains $_.Name })
    if ($files.Count -eq 0) { throw "No licence or notice file for $id $version in $folder" }
    foreach ($file in $files) {
        [void]$notices.AppendLine().AppendLine(('=' * 78)).AppendLine("$id $version - $($file.Name)").AppendLine(('=' * 78))
        [void]$notices.AppendLine([IO.File]::ReadAllText($file.FullName))
    }
}

# What may ship: the assets of the libraries in flevert.deps.json (the packages and runtime packs above, and Flevert's
# own projects), flevert's host and settings files, and the files of each "notices" entry in the manifest. The
# installer packs the whole folder but symbol files (Flevert.iss), so anything else would ship without its licence.
# openssl.cnf is Flevert's own (MIT, like the rest of the repository): the empty OpenSSL configuration qpdf is pointed
# at (ADR-0008), copied into the build by Flevert.Pdf.csproj.
$covered = @{} # case-insensitive, as file names are
foreach ($own in 'flevert.exe', 'flevert.deps.json', 'flevert.runtimeconfig.json', 'THIRD-PARTY-NOTICES.txt', 'LICENSE.txt', 'openssl.cnf') {
    $covered[$own] = $true
}
$target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name]
if (-not $target) { throw "flevert.deps.json has no target named $($deps.runtimeTarget.name)." }
foreach ($library in $target.Value.PSObject.Properties) {
    foreach ($kind in 'runtime', 'native', 'resources') {
        $assets = $library.Value.PSObject.Properties[$kind]
        if (-not $assets) { continue }
        foreach ($asset in $assets.Value.PSObject.Properties) {
            # dotnet publish puts assets in the folder itself, and satellite assemblies in a folder named after their culture.
            $name = Split-Path $asset.Name -Leaf
            $locale = $asset.Value.PSObject.Properties['locale']
            if ($locale) { $name = Join-Path $locale.Value $name }
            $covered[$name] = $true
        }
    }
}

$entries = foreach ($list in @($Manifest) + @(Join-Path $Root 'assets\notices.json' | Where-Object { Test-Path -LiteralPath $_ })) {
    $listed = ([IO.File]::ReadAllText($list) | ConvertFrom-Json).PSObject.Properties['notices']
    foreach ($entry in @(if ($listed) { $listed.Value })) { [pscustomobject]@{ Manifest = $list; Entry = $entry } }
}
foreach ($listing in @($entries)) {
    $entry = $listing.Entry
    foreach ($file in $entry.files) {
        # A wildcard covers a folder of like files, such as the menu icons (icons\*.ico); it must still match one.
        $shipped = @(if ($file.Contains('*')) {
                Get-ChildItem -Path (Join-Path ([Management.Automation.WildcardPattern]::Escape($publishRoot)) $file) -File -Force -ErrorAction SilentlyContinue |
                    ForEach-Object { $_.FullName.Substring($publishRoot.Length + 1) }
            } elseif (Test-Path -LiteralPath (Join-Path $publishRoot $file) -PathType Leaf) { $file })
        # An entry left behind by an update would otherwise cover a renamed binary by its old name.
        if ($shipped.Count -eq 0) {
            throw "$($listing.Manifest) lists $file under ""$($entry.title)"", but the publish folder has no such file: make the notices match what the build ships."
        }
        foreach ($name in $shipped) { $covered[$name] = $true }
    }
    $preface = @("Shipped as: $($entry.files -join ', ')")
    if ($entry.PSObject.Properties['note']) { $preface += $entry.note }
    foreach ($pattern in $entry.licences) {
        # Only the pattern from the manifest is a wildcard, not the folder it is in. -Force: hidden files count.
        $matched = @(Get-ChildItem -Path (Join-Path ([Management.Automation.WildcardPattern]::Escape($Root)) $pattern) -File -Force -ErrorAction SilentlyContinue |
            Sort-Object Name)
        if ($matched.Count -eq 0) {
            throw "Nothing matches $pattern, a licence of ""$($entry.title)"" listed in $($listing.Manifest). Run scripts\fetch-deps.ps1, or fix the notices."
        }
        foreach ($licence in $matched) {
            [void]$notices.AppendLine().AppendLine(('=' * 78)).AppendLine("$($entry.title) - $($licence.Name)").AppendLine(('=' * 78))
            foreach ($line in $preface) { [void]$notices.AppendLine($line) }
            if ($preface) { [void]$notices.AppendLine() }
            $preface = @()
            # ReadAllText: UTF-8 unless the file says otherwise, under Windows PowerShell too.
            [void]$notices.AppendLine([IO.File]::ReadAllText($licence.FullName))
        }
    }
}

# -Force: Inno packs hidden and system files too.
$unlicensed = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Force | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { $_.FullName.Substring($publishRoot.Length + 1) } | Where-Object { -not $covered.ContainsKey($_) })
if ($unlicensed.Count -gt 0) {
    throw ("No licence notice for {0} in {1}. The installer would ship them without their licences: add them to ""notices"" " +
        "in scripts\deps.json with their licence files, or keep them out of the build.") -f ($unlicensed -join ', '), $publishRoot
}
[IO.File]::WriteAllText((Join-Path $Publish 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), [Text.UTF8Encoding]::new($false))
