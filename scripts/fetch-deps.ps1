<#
.SYNOPSIS
  Fetches the third-party binaries Flevert ships (pdfium, qpdf) and their licences into deps\, each checked against the
  SHA256 pinned in scripts\deps.json before anything is unpacked.
.DESCRIPTION
  Nothing in deps\ is committed. Every download goes into a fresh temporary folder; deps\ is only changed after every item
  has been downloaded, verified and unpacked, so a failure leaves it as it was. A run with an unchanged manifest and a
  complete deps\ downloads nothing. ADR-0008.
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

$manifestHash = (Get-FileHash $Manifest -Algorithm SHA256).Hash.ToLowerInvariant()
$items = @((Get-Content $Manifest -Raw | ConvertFrom-Json).items)
$stamp = Join-Path $Destination '.fetched'

function Present($item) {
    if ($item.kind -eq 'file') { return Test-Path (Join-Path $Destination $item.target) }
    return Test-Path (Join-Path $Destination $item.name)
}

if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $manifestHash -and -not ($items | Where-Object { -not (Present $_) })) {
    'deps are up to date'
    return
}

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
    foreach ($folder in Get-ChildItem $stage -Directory) {
        $existing = Join-Path $Destination $folder.Name
        if (Test-Path $existing) { Remove-Item $existing -Recurse -Force }
        Copy-Item $folder.FullName $existing -Recurse
    }
    Set-Content $stamp $manifestHash -Encoding ascii
    "deps fetched into $Destination"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
