<#
.SYNOPSIS
  Builds a static, standalone tesseract.exe using vcpkg on Windows with only permissive libraries.
.DESCRIPTION
  Builds Tesseract 5.5.3 and Leptonica 1.87.0 with all dependencies statically linked (/MT CRT, static libraries).
  Contains no GPL or LGPL libraries (no jbigkit, no libiconv/libintl/libunistring/libidn2), and neither libcurl nor
  libarchive: an overlay of the pinned tesseract port builds it with DISABLE_CURL and DISABLE_ARCHIVE.
  Embeds a manifest setting the process code page to UTF-8, so paths and TESSDATA_PREFIX with letters outside the
  ANSI code page work (a Vietnamese user's profile, which holds %TEMP% and the per-user install folder).
  Fails on a vcpkg port or feature not on the allow-list below, on any import that is not a Microsoft system DLL or
  that is a network DLL, on a version mismatch, or when a smoke OCR run from a folder with Vietnamese letters does
  not read the text. Packages tesseract.exe, port licenses, and README.txt into a zip archive with SHA256 checksums.
  ADR-0011.
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

# Every port tesseract.exe may hold, with the features it may have, for the target triplet; the host triplet may only
# hold vcpkg's own CMake helpers, which build nothing into the binary. Each target port's licence goes into the zip's
# licenses/ folder and into THIRD-PARTY-NOTICES.txt through scripts/deps.json, so a port or feature a vcpkg bump brings
# in must be reviewed (licence, network or archive code, security record) and added here and to the notice there.
$allowedTargetPorts = @{
    'giflib'        = @()
    'leptonica'     = @()
    'libjpeg-turbo' = @()
    'liblzma'       = @()
    'libpng'        = @()
    'libspng'       = @()
    'libwebp'       = @('libwebpmux', 'nearlossless', 'simd', 'unicode')
    'openjpeg'      = @()
    'tesseract'     = @()
    'tiff'          = @('jpeg', 'lzma', 'zip')
    'zlib'          = @()
}
$allowedHostPorts = @('vcpkg-cmake', 'vcpkg-cmake-config', 'vcpkg-cmake-get-vars')

# DLLs for networking, TLS, certificates and XML that a build with libcurl, libarchive or ScrollView imports. They are signed
# system DLLs, so the System32 check below would let them through: listed here, they fail the build instead.
$deniedImports = @(
    'ws2_32.dll', 'wsock32.dll', 'mswsock.dll', 'iphlpapi.dll', 'dnsapi.dll', 'secur32.dll', 'sspicli.dll',
    'crypt32.dll', 'ncrypt.dll', 'wininet.dll', 'winhttp.dll', 'urlmon.dll', 'wldap32.dll', 'normaliz.dll',
    'xmllite.dll'
)

$work = Join-Path ([IO.Path]::GetTempPath()) ("tesseract-build-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $work | Out-Null

function Get-SdkTool([string]$Name) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if ($Name -eq 'dumpbin.exe' -and (Test-Path -LiteralPath $vswhere)) {
        $vsPath = & $vswhere -latest -property installationPath
        if ($vsPath) {
            $found = @(Get-ChildItem -Path "$vsPath\VC\Tools\MSVC\*\bin\Hostx64\x64\$Name" -ErrorAction SilentlyContinue)
            if ($found.Count -gt 0) { return $found[-1].FullName }
        }
    }
    if ($Name -eq 'mt.exe') {
        $found = @(Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\$Name" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name })
        if ($found.Count -gt 0) { return $found[-1].FullName }
    }
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "$Name could not be located"
}

# Replaces $Old (a regular expression) in $Text exactly once, or fails: a port that changed upstream must be looked at
# again, not patched blindly.
function Set-Once([string]$Text, [string]$Old, [string]$New, [string]$What) {
    $count = ([regex]::Matches($Text, $Old)).Count
    if ($count -ne 1) { throw "Overlay port: expected one match for $What, found $count. Review the port at the new commit." }
    return [regex]::Replace($Text, $Old, $New)
}

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

    # Flevert only ever hands tesseract.exe image files it wrote itself, so the URL input (libcurl) and the image list
    # in an archive (libarchive) are code that only an attacker could reach. The port at the pinned commit requires both;
    # this overlay is that same port, with the two dependencies dropped and Tesseract's own switches turned on. It also
    # turns off ScrollView, Tesseract's debug viewer, which a config variable can make connect to a TCP server (and
    # start java to run one); it is the only user of Winsock, which Tesseract otherwise links on Windows.
    "Writing an overlay of the tesseract port without libcurl and libarchive..."
    $overlayDir = Join-Path $work 'overlay-ports'
    $overlayPort = Join-Path $overlayDir 'tesseract'
    New-Item -ItemType Directory -Force $overlayDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $vcpkgDir 'ports\tesseract') -Destination $overlayPort -Recurse

    $manifestPath = Join-Path $overlayPort 'vcpkg.json'
    $portManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($portManifest.version -ne $TesseractVersion) {
        throw "The tesseract port at $VcpkgCommit is $($portManifest.version), not $TesseractVersion."
    }
    $dropped = @('curl', 'libarchive')
    $kept = @($portManifest.dependencies | Where-Object {
            $name = if ($_ -is [string]) { $_ } else { $_.name }
            $dropped -notcontains $name
        })
    if ($kept.Count -ne @($portManifest.dependencies).Count - $dropped.Count) {
        throw "Overlay port: the tesseract port no longer depends on both curl and libarchive. Review the port at the new commit."
    }
    $portManifest.dependencies = $kept
    [IO.File]::WriteAllText($manifestPath, ($portManifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))

    $portfilePath = Join-Path $overlayPort 'portfile.cmake'
    $portfile = [IO.File]::ReadAllText($portfilePath)
    $portfile = Set-Once $portfile '-DCMAKE_REQUIRE_FIND_PACKAGE_LibArchive=ON' '-DDISABLE_ARCHIVE=ON' 'the libarchive option'
    $portfile = Set-Once $portfile '-DCMAKE_REQUIRE_FIND_PACKAGE_CURL=ON' '-DDISABLE_CURL=ON -DGRAPHICS_DISABLED=ON' 'the curl option'
    $portfile = Set-Once $portfile 'find_dependency\(CURL\)\r?\n' '' 'find_dependency(CURL)'
    $portfile = Set-Once $portfile 'find_dependency\(LibArchive\)\r?\n' '' 'find_dependency(LibArchive)'
    [IO.File]::WriteAllText($portfilePath, $portfile, [Text.UTF8Encoding]::new($false))

    "Installing tesseract:x64-windows-static-release with overlay triplets from $tripletDir..."
    & $vcpkgExe install tesseract:x64-windows-static-release "--overlay-triplets=$tripletDir" "--overlay-ports=$overlayDir" --clean-after-build
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install tesseract failed with exit code $LASTEXITCODE" }

    "Checking installed ports against the allow-list..."
    $listJson = (& $vcpkgExe list --x-json) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "vcpkg list failed with exit code $LASTEXITCODE" }
    $listJson
    $listed = $listJson | ConvertFrom-Json
    foreach ($entry in $listed.PSObject.Properties) {
        $package = $entry.Value
        $name = $package.package_name
        $features = @($package.features | Where-Object { $_ -and $_ -ne 'core' -and $_ -ne 'default' })
        if ($entry.Name -match '^[^\[:]+\[([^\]]+)\]:') { $features += $Matches[1] }
        if ($package.triplet -eq 'x64-windows-static-release') {
            if (-not $allowedTargetPorts.ContainsKey($name)) {
                throw "vcpkg installed port '$name', which is not on the allow-list. Review its licence and code, then add it here and to the Tesseract notice in scripts/deps.json."
            }
            foreach ($feature in $features) {
                if ($allowedTargetPorts[$name] -notcontains $feature) {
                    throw "vcpkg installed feature '$name[$feature]', which is not on the allow-list."
                }
            }
        }
        elseif ($allowedHostPorts -notcontains $name) {
            throw "vcpkg installed port '$name' for triplet '$($package.triplet)', which is not on the allow-list."
        }
    }
    "All installed ports are on the allow-list."

    $installed = Join-Path $vcpkgDir 'installed\x64-windows-static-release'
    $builtExe = Join-Path $installed 'tools\tesseract\tesseract.exe'
    if (-not (Test-Path -LiteralPath $builtExe)) { throw "tesseract.exe not found at $builtExe" }

    # $finalExe is the file that is packaged. mt.exe and dumpbin.exe get it at a plain path; the runs below use a copy
    # in a folder named with letters outside the ANSI code page, as Flevert's per-user install folder under
    # C:\Users\Dương\ is.
    $finalDir = Join-Path $work 'bin'
    New-Item -ItemType Directory -Force $finalDir | Out-Null
    $finalExe = Join-Path $finalDir 'tesseract.exe'
    Copy-Item -LiteralPath $builtExe -Destination $finalExe

    # Tesseract and Leptonica take paths and TESSDATA_PREFIX through the C runtime's char APIs (argv, fopen, getenv),
    # which use the process code page. activeCodePage makes it UTF-8 (Windows 10 1903 and later), so any path works.
    "Embedding a UTF-8 code page manifest..."
    $mt = Get-SdkTool 'mt.exe'
    "mt.exe: $mt"
    $utf8Manifest = Join-Path $work 'utf8.manifest'
    [IO.File]::WriteAllText($utf8Manifest, @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">UTF-8</activeCodePage>
    </windowsSettings>
  </application>
</assembly>
'@, [Text.UTF8Encoding]::new($false))
    $existingManifest = Join-Path $work 'existing.manifest'
    & $mt -nologo "-inputresource:$finalExe;#1" "-out:$existingManifest" | Out-Host
    $manifests = @($utf8Manifest)
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $existingManifest -PathType Leaf)) {
        "Existing manifest, merged with the code page:"
        Get-Content -LiteralPath $existingManifest | Out-Host
        $manifests = @($existingManifest, $utf8Manifest)
    }
    else {
        "tesseract.exe has no manifest yet."
    }
    & $mt -nologo -manifest @manifests "-outputresource:$finalExe;#1"
    if ($LASTEXITCODE -ne 0) { throw "mt.exe failed to embed the manifest with exit code $LASTEXITCODE" }
    $finalManifest = Join-Path $work 'final.manifest'
    & $mt -nologo "-inputresource:$finalExe;#1" "-out:$finalManifest"
    if ($LASTEXITCODE -ne 0) { throw "mt.exe could not read back the manifest with exit code $LASTEXITCODE" }
    $finalText = [IO.File]::ReadAllText($finalManifest)
    "Embedded manifest:"
    $finalText
    if ($finalText -notmatch '<(\w+:)?activeCodePage[^>]*>\s*UTF-8\s*</') { throw "The embedded manifest does not set activeCodePage to UTF-8." }

    $unicodeDir = Join-Path $work 'Cộng hòa'
    $smokeTessdata = Join-Path $unicodeDir 'tessdata'
    New-Item -ItemType Directory -Force $smokeTessdata | Out-Null
    $tesseractExe = Join-Path $unicodeDir 'tesseract.exe'
    Copy-Item -LiteralPath $finalExe -Destination $tesseractExe

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
    $dumpbin = Get-SdkTool 'dumpbin.exe'
    $dumpOutput = & $dumpbin /dependents $finalExe
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
    if ($deps.Count -eq 0) { throw "dumpbin found no dependencies for $finalExe" }

    "Dependencies of tesseract.exe ($($deps.Count) found):"
    $deps | ForEach-Object { "  $_" }

    foreach ($dll in $deps) {
        if ($deniedImports -contains $dll) {
            throw "Network, TLS or XML DLL '$dll' is imported: network code is linked in again. Build without it (DISABLE_CURL, DISABLE_ARCHIVE, GRAPHICS_DISABLED)."
        }
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

    "Running smoke OCR test with vie.traineddata from '$unicodeDir'..."
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
    $smokePng = Join-Path $unicodeDir 'ảnh thử.png'
    $bmp.Save($smokePng, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()

    # As Flevert runs it: the data folder named only by TESSDATA_PREFIX, the image and the output under the same folder.
    $smokeOutBase = Join-Path $unicodeDir 'kết quả'
    $previousPrefix = $env:TESSDATA_PREFIX
    $env:TESSDATA_PREFIX = $smokeTessdata
    try {
        $smokeRun = & $tesseractExe $smokePng $smokeOutBase -l vie 2>&1
        $smokeExit = $LASTEXITCODE
    }
    finally {
        $env:TESSDATA_PREFIX = $previousPrefix
    }
    if ($smokeExit -ne 0) { throw "Smoke OCR failed ($smokeExit): $smokeRun" }
    $smokeTxtFile = "$smokeOutBase.txt"
    if (-not (Test-Path -LiteralPath $smokeTxtFile)) { throw "Smoke OCR output file missing: $smokeTxtFile ($smokeRun)" }
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

    Copy-Item -LiteralPath $finalExe -Destination (Join-Path $stage 'tesseract.exe')

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
