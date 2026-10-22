<#
.SYNOPSIS
  Builds a static, standalone tesseract.exe using vcpkg on Windows with only permissive libraries.
.DESCRIPTION
  Builds Tesseract 5.5.3 and Leptonica 1.87.0 with all dependencies statically linked (/MT CRT, static libraries).
  Contains no GPL or LGPL libraries (no jbigkit, no libiconv/libintl/libunistring/libidn2).
  Validates that tesseract.exe depends only on Windows system DLLs, checks versions, runs smoke OCR,
  and packages tesseract.exe, port licenses, and README.txt into a zip archive with SHA256 checksums.
  ADR-0010.
#>
param(
    [string]$VcpkgCommit = '9eec002df1cd109f8f104cd966a763daf9751489',
    [string]$VcpkgTag = '2026.10.16',
    [string]$TesseractVersion = '5.5.3',
    [string]$LeptonicaVersion = '1.87.0',
    [string]$VieDataSha256 = '79df64caf7bcfb2a27df5042ecb6121e196eada34da774956995747636d5bfa1',
    [string]$ArtifactsDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\tesseract')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Split-Path -Parent $PSScriptRoot)
$tripletDir = Join-Path $repoRoot 'triplets'
$ArtifactsDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactsDir)

$work = Join-Path ([IO.Path]::GetTempPath()) ("tesseract-build-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $work | Out-Null

try {
    $vcpkgDir = Join-Path $work 'vcpkg'
    # Only the pinned commit, fetched by its hash: what is built cannot move with the tag or the branch.
    "Fetching vcpkg at pinned commit $VcpkgCommit (tag $VcpkgTag)..."
    New-Item -ItemType Directory -Force $vcpkgDir | Out-Null
    Push-Location $vcpkgDir
    try {
        & git init --quiet
        if ($LASTEXITCODE -ne 0) { throw "git init failed with exit code $LASTEXITCODE" }
        & git fetch --quiet --depth 1 https://github.com/microsoft/vcpkg.git $VcpkgCommit
        if ($LASTEXITCODE -ne 0) { throw "git fetch $VcpkgCommit failed with exit code $LASTEXITCODE" }
        & git checkout --quiet FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { throw "git checkout $VcpkgCommit failed with exit code $LASTEXITCODE" }
        if ((& git rev-parse HEAD) -ne $VcpkgCommit) { throw "vcpkg is not at $VcpkgCommit." }
        & .\bootstrap-vcpkg.bat -disableMetrics
        if ($LASTEXITCODE -ne 0) { throw "bootstrap-vcpkg failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    $vcpkgExe = Join-Path $vcpkgDir 'vcpkg.exe'
    if (-not (Test-Path -LiteralPath $vcpkgExe)) { throw "vcpkg.exe not found at $vcpkgExe" }

    "Installing tesseract:x64-windows-static-release with overlay triplets from $tripletDir..."
    & $vcpkgExe install tesseract:x64-windows-static-release "--overlay-triplets=$tripletDir" --clean-after-build
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install tesseract failed with exit code $LASTEXITCODE" }

    $installed = Join-Path $vcpkgDir 'installed\x64-windows-static-release'
    $tesseractExe = Join-Path $installed 'tools\tesseract\tesseract.exe'
    if (-not (Test-Path -LiteralPath $tesseractExe)) { throw "tesseract.exe not found at $tesseractExe" }

    "Validating tesseract version output..."
    $versionLines = @(& $tesseractExe --version 2>&1)
    $versionOutput = $versionLines -join "`n"
    $versionOutput
    if ($versionOutput -notmatch "tesseract\s+$([regex]::Escape($TesseractVersion))") {
        throw "tesseract version mismatch: expected $TesseractVersion, got:`n$versionOutput"
    }
    if ($versionOutput -notmatch "leptonica-$([regex]::Escape($LeptonicaVersion))") {
        throw "leptonica version mismatch: expected $LeptonicaVersion, got:`n$versionOutput"
    }

    "Checking dependencies with dumpbin..."
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $dumpbin = $null
    if (Test-Path -LiteralPath $vswhere) {
        $vsPath = & $vswhere -latest -property installationPath
        if ($vsPath) {
            $found = @(Get-ChildItem -Path "$vsPath\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe" -ErrorAction SilentlyContinue)
            if ($found.Count -gt 0) { $dumpbin = $found[-1].FullName }
        }
    }
    if (-not $dumpbin) {
        $cmd = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
        if ($cmd) { $dumpbin = $cmd.Source }
    }
    if (-not $dumpbin) { throw "dumpbin.exe could not be located" }

    $dumpOutput = & $dumpbin /dependents $tesseractExe
    $lines = $dumpOutput -split "`r?`n"
    $inDeps = $false
    $deps = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        if ($line -match 'Image has the following dependencies:') {
            $inDeps = $true
            continue
        }
        if ($inDeps) {
            if ($line -match '^\s+([A-Za-z0-9._-]+\.dll)\s*$' -or $line -match '^\s+([A-Za-z0-9._-]+\.drv)\s*$') {
                $deps.Add($Matches[1].ToLowerInvariant())
            }
            elseif ($line -match '^\s*Summary' -or ($deps.Count -gt 0 -and [string]::IsNullOrWhiteSpace($line))) {
                break
            }
        }
    }
    if ($deps.Count -eq 0) { throw "dumpbin found no dependencies for $tesseractExe" }

    "Dependencies of tesseract.exe ($($deps.Count) found):"
    $deps | ForEach-Object { "  $_" }

    foreach ($dll in $deps) {
        if ($dll -match '^(api-ms-win-|ext-ms-)') { continue }
        # The Visual C++ redistributable may sit in System32 on the runner, but not on a user's PC: its DLLs (the CRT,
        # OpenMP, the concurrency runtime) must be linked in, not imported.
        if ($dll -match '^(vcruntime|msvcp|vcomp|concrt|vccorlib|mfc)') {
            throw "Visual C++ runtime DLL '$dll' is imported. Expected static CRT linkage (/MT) and no OpenMP runtime."
        }
        $sysPath = Join-Path "$env:SystemRoot\System32" $dll
        if (-not (Test-Path -LiteralPath $sysPath -PathType Leaf)) {
            throw "Non-system DLL dependency found: '$dll' (not in $env:SystemRoot\System32)"
        }
        $sig = Get-AuthenticodeSignature -LiteralPath $sysPath
        if ($sig.Status -ne 'Valid' -or "$($sig.SignerCertificate.Subject)" -notmatch 'Microsoft Corporation') {
            throw "DLL '$dll' is not a signed Microsoft system DLL (status=$($sig.Status), signer=$($sig.SignerCertificate.Subject))"
        }
    }
    "All dependencies are valid Windows system DLLs."

    "Running smoke OCR test with vie.traineddata..."
    $smokeDir = Join-Path $work 'smoke'
    $smokeTessdata = Join-Path $smokeDir 'tessdata'
    New-Item -ItemType Directory -Force $smokeTessdata | Out-Null
    $vieDest = Join-Path $smokeTessdata 'vie.traineddata'
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/vie.traineddata' -OutFile $vieDest -UseBasicParsing
    $actualVieHash = (Get-FileHash -LiteralPath $vieDest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualVieHash -ne $VieDataSha256) {
        throw "vie.traineddata SHA256 mismatch: expected $VieDataSha256, got $actualVieHash"
    }

    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 400, 100
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $font = New-Object System.Drawing.Font "Arial", 22
    $brush = [System.Drawing.Brushes]::Black
    $g.DrawString("Cộng hòa xã hội", $font, $brush, 10, 25)
    $smokePng = Join-Path $smokeDir 'smoke.png'
    $bmp.Save($smokePng, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()

    $smokeOutBase = Join-Path $smokeDir 'smoke_out'
    $smokeRun = & $tesseractExe $smokePng $smokeOutBase --tessdata-dir $smokeTessdata -l vie 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Smoke OCR failed ($LASTEXITCODE): $smokeRun" }
    $smokeTxtFile = "$smokeOutBase.txt"
    if (-not (Test-Path -LiteralPath $smokeTxtFile)) { throw "Smoke OCR output file missing: $smokeTxtFile" }
    $smokeText = [IO.File]::ReadAllText($smokeTxtFile, [Text.Encoding]::UTF8)
    "Smoke OCR result: [$($smokeText.Trim())]"
    if ($smokeText -notmatch 'Cộng\s*hòa') {
        throw "Smoke OCR output failed to recognize Vietnamese text: '$smokeText'"
    }
    "Smoke OCR passed."

    "Packaging release zip..."
    $stage = Join-Path $work 'stage'
    $stageLicenses = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Force $stageLicenses | Out-Null

    Copy-Item -LiteralPath $tesseractExe -Destination (Join-Path $stage 'tesseract.exe')

    $shareDir = Join-Path $installed 'share'
    foreach ($dir in Get-ChildItem -LiteralPath $shareDir -Directory) {
        $port = $dir.Name
        $copyright = Join-Path $dir.FullName 'copyright'
        if (Test-Path -LiteralPath $copyright -PathType Leaf) {
            Copy-Item -LiteralPath $copyright -Destination (Join-Path $stageLicenses "$port-copyright")
        }
    }

    $vcpkgList = @(& $vcpkgExe list --triplet x64-windows-static-release)
    [IO.File]::WriteAllLines((Join-Path $stage 'README.txt'), $vcpkgList, [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Force $ArtifactsDir | Out-Null
    $zipName = "tesseract-$TesseractVersion-vcpkg-$VcpkgTag-win64-static.zip"
    $zipPath = Join-Path $ArtifactsDir $zipName
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumsPath = Join-Path $ArtifactsDir 'SHA256SUMS.txt'
    "$zipHash  $zipName" | Out-File -LiteralPath $sumsPath -Encoding ascii
    "Successfully built and packaged $zipName"
    "SHA256: $zipHash"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
