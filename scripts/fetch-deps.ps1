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

# A new folder that only this user, SYSTEM and Administrators can change, whatever its parent passes down. %TEMP% may
# let other accounts change what is created in it (on the maintainer's machine it grants a sandbox Modify): they could
# swap a download after its hash check, or a staged file before it is copied into deps\, and the tests then run it.
function New-PrivateFolder([string]$Path) {
    $trusted = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]'S-1-5-18', # SYSTEM
        [Security.Principal.SecurityIdentifier]'S-1-5-32-544') # Administrators
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false) # inherits nothing
    foreach ($sid in $trusted) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    # Created with that ACL, so the entries of the parent never apply to it, not even for a moment.
    $aclExtensions = 'System.IO.FileSystemAclExtensions' -as [type]
    if ($aclExtensions) { $aclExtensions::Create([IO.DirectoryInfo]::new($Path), $security) } # PowerShell 7 (.NET)
    else { [void][IO.Directory]::CreateDirectory($Path, $security) } # Windows PowerShell (.NET Framework)
    # Both return quietly when the folder exists: make sure it is the private, empty folder asked for.
    $acl = Get-Acl -LiteralPath $Path
    $others = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $trusted -notcontains $_.IdentityReference })
    if (-not $acl.AreAccessRulesProtected -or $others.Count -gt 0 -or
        $trusted -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]) -or
        @(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
        throw "$Path is not a new folder of this user's own. Nothing was changed in $Destination."
    }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash($Stream) | ForEach-Object { $_.ToString('x2') }) }
    finally { $sha.Dispose() }
}

# Deletes a file or folder without following links: a junction or symbolic link inside is removed itself, never what
# it points to. Windows PowerShell 5.1.26100's Remove-Item -Recurse did not follow a junction when probed, but that is
# one build, and symbolic links were not probed.
function Remove-Tree([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item) { Remove-Entry $item }
}

function Remove-Entry([IO.FileSystemInfo]$Entry) {
    if (-not ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        if ($Entry -is [IO.DirectoryInfo]) {
            foreach ($child in $Entry.GetFileSystemInfos()) { Remove-Entry $child }
        }
        elseif ($Entry.Attributes -band [IO.FileAttributes]::ReadOnly) {
            $Entry.Attributes = $Entry.Attributes -band -bnot [IO.FileAttributes]::ReadOnly
        }
    }
    $Entry.Delete() # for a link, the link only
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("flevert-deps-" + [guid]::NewGuid())
New-PrivateFolder $work
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory $stage | Out-Null
try {
    foreach ($item in $items) {
        # Expand-Archive insists on a .zip extension.
        $download = Join-Path $work ($item.name + '.' + $item.kind)
        $uri = [Uri]$item.url
        # Tests point the manifest at local files or a loopback server; real downloads are https (FetchDepsScriptTests
        # checks the manifest).
        if ($uri.IsFile) { Copy-Item -LiteralPath $uri.LocalPath $download }
        else { Invoke-WebRequest $uri -OutFile $download -UseBasicParsing }

        # Held open, refusing writers, deleters and renames, from the hash check until it is unpacked, so what is
        # unpacked is what was checked. tar and Expand-Archive only read it, which this sharing allows.
        $held = [IO.File]::Open($download, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $actual = Get-StreamSha256 $held
            if ($actual -ne $item.sha256) {
                throw "SHA256 mismatch for $($item.name): expected $($item.sha256), got $actual. Nothing was changed in $Destination."
            }

            if ($item.kind -eq 'file') {
                $target = Join-Path $stage $item.target
                New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
                $held.Position = 0
                $copy = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $held.CopyTo($copy) } finally { $copy.Dispose() }
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
        }
        finally { $held.Dispose() }

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

    # Everything is verified and unpacked: only now replace what deps\ held for these names. The stamp gets the hashes of
    # the staged files, and the copies must match them.
    $folders = @(Get-ChildItem -LiteralPath $stage -Directory | ForEach-Object Name)
    $kept = [ordered]@{}
    foreach ($file in @($folders | ForEach-Object { Get-TreeFiles $stage $_ } | Sort-Object)) {
        $kept[$file] = Get-Sha256 (Join-Path $stage $file)
    }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($folder in $folders) {
        $existing = Join-Path $Destination $folder
        Remove-Tree $existing
        Copy-Item -LiteralPath (Join-Path $stage $folder) $existing -Recurse
    }
    $copied = @($folders | ForEach-Object { Get-TreeFiles $Destination $_ } | Sort-Object)
    $changed = @($kept.Keys | Where-Object { $copied -notcontains $_ -or (Get-Sha256 (Join-Path $Destination $_)) -ne $kept[$_] }) +
        @($copied | Where-Object { -not $kept.Contains($_) })
    if ($changed.Count -gt 0) { throw "$($changed -join ', ') in $Destination changed while being copied; run this script again." }
    [IO.File]::WriteAllLines($stamp, [string[]](@($manifestHash) + @($kept.Keys | ForEach-Object { "$($kept[$_])  $_" })))
    "deps fetched into $Destination"
}
finally {
    try { Remove-Tree $work }
    catch { Write-Warning "Could not remove $work ($_)." }
}
