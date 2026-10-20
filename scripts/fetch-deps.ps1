<#
.SYNOPSIS
  Fetches the third-party binaries Flevert ships (pdfium, qpdf) and their licences into deps\, each checked against the
  SHA256 pinned in scripts\deps.json before anything is unpacked.
.DESCRIPTION
  Nothing in deps\ is committed. Every download goes into a fresh temporary folder; deps\ is only changed after every item
  has been downloaded, verified and unpacked, so a failure leaves it as it was. deps\.fetched records the manifest's
  hash and the SHA256 of every file kept; a run downloads nothing only when the manifest is unchanged and deps\ holds
  exactly those files, unchanged. ADR-0008.
.PARAMETER Manifest
  The list of downloads; defaults to scripts\deps.json.
.PARAMETER Destination
  Where to put them; defaults to deps\ at the repository root.
#>
param(
    [string]$Manifest = (Join-Path $PSScriptRoot 'deps.json'),
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'deps')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower

# .NET resolves relative paths against its own current folder, not PowerShell's.
$Manifest = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Manifest)
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
$manifestHash = (Get-FileHash -LiteralPath $Manifest -Algorithm SHA256).Hash.ToLowerInvariant()
$items = @((Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json).items)
$stamp = Join-Path $Destination '.fetched'

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Every file under $Base\$Folder, as a path relative to $Base. Fails on a junction or symbolic link instead of
# following it: nothing this script unpacks has one, so it was put there by something else.
function Get-TreeFiles([string]$Base, [string]$Folder) {
    $dir = [IO.DirectoryInfo]::new((Join-Path $Base $Folder))
    if (-not $dir.Exists) { return }
    if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$($dir.FullName) is a link." }
    foreach ($entry in $dir.GetFileSystemInfos()) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$($entry.FullName) is a link." }
        $relative = Join-Path $Folder $entry.Name
        if ($entry -is [IO.DirectoryInfo]) { Get-TreeFiles $Base $relative } else { $relative }
    }
}

# deps\ is up to date when the stamp names this manifest and the folders it lists hold exactly the files it lists, each
# with the recorded hash. The build itself only checks that pdfium.dll and qpdf.exe exist, and copies every DLL in
# deps\qpdf\bin next to flevert.exe, so a missing, changed or added file must all lead to a fresh fetch.
function Get-Difference {
    if (-not (Test-Path -LiteralPath $stamp -PathType Leaf)) { return 'no earlier fetch' }
    $lines = @([IO.File]::ReadAllLines($stamp))
    if ($lines.Count -lt 2 -or $lines[0] -ne $manifestHash) { return 'scripts\deps.json changed' }
    $recorded = @{}
    foreach ($line in $lines[1..($lines.Count - 1)]) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { return 'the stamp is damaged' }
        $recorded[$Matches[2]] = $Matches[1]
    }
    try {
        $present = @($recorded.Keys | ForEach-Object { ($_ -split '\\')[0] } | Sort-Object -Unique |
            ForEach-Object { Get-TreeFiles $Destination $_ })
    }
    catch { return $_.Exception.Message }
    foreach ($file in $present) {
        if (-not $recorded.ContainsKey($file)) { return "$file is not from the last fetch" }
    }
    foreach ($file in $recorded.Keys) {
        $path = Join-Path $Destination $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "$file is missing" }
        if ((Get-Sha256 $path) -ne $recorded[$file]) { return "$file changed" }
    }
    return $null
}

$difference = Get-Difference
if (-not $difference) {
    'deps are up to date'
    return
}
"Fetching deps: $difference."

$work = Join-Path ([IO.Path]::GetTempPath()) ("flevert-deps-" + [guid]::NewGuid())
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory $stage | Out-Null
try {
    foreach ($item in $items) {
        # Expand-Archive insists on a .zip extension.
        $download = Join-Path $work ($item.name + '.' + $item.kind)
        $uri = [Uri]$item.url
        # Tests point the manifest at local files; real downloads are https (FetchDepsScriptTests checks the manifest).
        if ($uri.IsFile) { Copy-Item -LiteralPath $uri.LocalPath $download }
        else { Invoke-WebRequest $uri -OutFile $download -UseBasicParsing }

        $actual = (Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $item.sha256) {
            throw "SHA256 mismatch for $($item.name): expected $($item.sha256), got $actual. Nothing was changed in $Destination."
        }

        if ($item.kind -eq 'file') {
            $target = Join-Path $stage $item.target
            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $download $target
            continue
        }

        $unpacked = Join-Path $work ($item.name + '.unpacked')
        New-Item -ItemType Directory $unpacked | Out-Null
        switch ($item.kind) {
            'zip' { Expand-Archive -LiteralPath $download -DestinationPath $unpacked }
            'tgz' {
                & "$env:SystemRoot\System32\tar.exe" -xzf $download -C $unpacked
                if ($LASTEXITCODE -ne 0) { throw "Could not unpack $($item.name) ($LASTEXITCODE)." }
            }
            default { throw "Unknown kind '$($item.kind)' for $($item.name)." }
        }

        $root = Join-Path $unpacked $item.strip
        foreach ($pattern in $item.keep) {
            $matched = @(Get-ChildItem -Path (Join-Path $root $pattern) -File -ErrorAction SilentlyContinue)
            if ($matched.Count -eq 0) { throw "$($item.name): nothing matches $pattern in the download." }
            foreach ($file in $matched) {
                $relative = $file.FullName.Substring((Resolve-Path $root).Path.TrimEnd('\').Length + 1)
                $target = Join-Path (Join-Path $stage $item.name) $relative
                New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
                Copy-Item -LiteralPath $file.FullName $target
            }
        }
    }

    # Everything is verified and unpacked: only now replace what deps\ held for these names.
    New-Item -ItemType Directory -Force $Destination | Out-Null
    $folders = @(Get-ChildItem -LiteralPath $stage -Directory | ForEach-Object Name)
    foreach ($folder in $folders) {
        $existing = Join-Path $Destination $folder
        if (Test-Path $existing) { Remove-Item $existing -Recurse -Force }
        Copy-Item (Join-Path $stage $folder) $existing -Recurse
    }
    $kept = @($folders | ForEach-Object { Get-TreeFiles $Destination $_ } | Sort-Object)
    [IO.File]::WriteAllLines($stamp, [string[]](@($manifestHash) + @($kept | ForEach-Object { "$(Get-Sha256 (Join-Path $Destination $_))  $_" })))
    "deps fetched into $Destination"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
