<#
.SYNOPSIS
End-to-end test of the installer on this machine: installs silently without admin, checks files, licences, menu and
real conversions (a picture, PDF operations through the bundled qpdf and pdfium, pictures combined into a PDF, a sound
through the bundled ffmpeg), installs
over itself, uninstalls and checks that nothing Flevert wrote is left. Further scenarios cover the update-check
choice, the uninstall safety net, a classic-menu key the user created, a refused install folder, /DIR, uninstalling
while flevert.exe runs, an install folder that is a junction, one that already holds someone else's files, setup
started from a junction, and an upgrade over the previous release (downloaded and checked by get-previous-setup.ps1).

Uses the real per-user install location and HKCU, so it refuses to run when anything of Flevert is present.

.PARAMETER Setup
The setup executable; defaults to the newest artifacts\installer\Flevert-Setup-*.exe.

.PARAMETER PreviousSetup
The previous release's setup, downloaded earlier, for the upgrade scenario. Without it the scenario downloads it. Either
way it is copied into a private folder and must have the SHA256 pinned in get-previous-setup.ps1.
#>
param([string]$Setup, [string]$PreviousSetup)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}_is1'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Flevert'
$appData = Join-Path $env:LOCALAPPDATA 'Flevert'
$classes = 'HKCU:\Software\Classes'
$associations = "$classes\SystemFileAssociations"
$classicKey = "$classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"
$heic = Join-Path $root 'tests\Flevert.Image.Tests\fixtures\gradient-320x240.heic'
$rotationsPdf = Join-Path $root 'tests\Flevert.Pdf.Tests\fixtures\rotations.pdf'
# Every folder flevert.exe writes under app data (AppData.Subfolders); the uninstaller deletes exactly these.
$appDataFolders = 'logs', 'pipes', 'magick', 'update', 'media'
# Each "notices" entry of deps.json: files from deps\ the installer ships, with the title their licences carry.
$bundled = @(([IO.File]::ReadAllText((Join-Path $root 'scripts\deps.json')) | ConvertFrom-Json).notices)
$bundledFiles = @($bundled | ForEach-Object { $_.files } | Sort-Object -Unique)
# What only a conversion loads: setup must not unpack it into %TEMP% to check the install folder (Flevert.iss, [Files]).
# Inno's log names an unpacked file without its folder, so the files of the ffmpeg folder are listed by name too.
$conversionOnly = $bundledFiles + @($bundledFiles | ForEach-Object { Split-Path $_ -Leaf }) + 'openssl.cnf', 'Magick.Native-*.dll'

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "  ok  $Message"
}

function MenuKeys {
    # Every key named after the app, not only the ones today's code writes, so a new kind of key cannot be left behind.
    # Flevert, and Flevert: more PDF tools (shell\Flevert.Tools) for PDFs; any other shell key named after the app counts too.
    @(Get-ChildItem $associations -ErrorAction SilentlyContinue |
        Where-Object { @(Get-ChildItem "$($_.PSPath)\shell" -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'Flevert*' }).Count -gt 0 }) +
    @(Get-ChildItem $classes -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'Flevert*' })
}

function AssociationNames { @(Get-ChildItem $associations -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName }) }

function RunningFlevert { @(Get-Process flevert -ErrorAction SilentlyContinue) }

function StopInstalledFlevert {
    foreach ($p in RunningFlevert | Where-Object { $_.Path -like "$app\*" }) {
        try { $p.Kill(); $p.WaitForExit(10000) | Out-Null } catch { Write-Warning "could not stop flevert.exe $($p.Id): $_" }
    }
}

function SetupRefused($Run, [string]$Reason) {
    # Inno's exit code when PrepareToInstall refuses.
    Assert ($Run.ExitCode -eq 7) "setup refuses with exit code 7 (got $($Run.ExitCode); log $($Run.Log))"
    Assert ((Get-Content $Run.Log -Raw).Contains($Reason)) "setup logs why: '$Reason'"
}

function Setup([string[]]$Arguments, [string]$WorkingDirectory = $work, [string]$Exe = $Setup) {
    $log = Join-Path $work "setup-$([guid]::NewGuid()).log"
    $all = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $Arguments
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Exe -ArgumentList $all -WorkingDirectory $WorkingDirectory -Wait -PassThru
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Log = $log; Seconds = $timer.Elapsed.TotalSeconds }
}

function Install([string]$ClassicMenu = '', [string]$UpdateCheck = '') {
    $arguments = @()
    if ($ClassicMenu) { $arguments += "/CLASSICMENU=$ClassicMenu" }
    if ($UpdateCheck) { $arguments += "/UPDATECHECK=$UpdateCheck" }
    $run = Setup $arguments
    Assert ($run.ExitCode -eq 0) "setup exits with 0 (got $($run.ExitCode); log $($run.Log))"
    Write-Host ("      installed in {0:N0} s" -f $run.Seconds)
    return $run
}

function WaitForUninstaller {
    # unins000.exe hands over to a copy of itself in %TEMP%\is-*-uninstall.tmp and returns at once.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Process | Where-Object { $_.Path -like '*-uninstall.tmp\_unins.tmp' }) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
}

function Uninstall {
    Assert (Test-Path "$app\unins000.exe") 'the uninstaller is installed'
    Start-Process "$app\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Milliseconds 500
    WaitForUninstaller
}

function AssertClean {
    Assert (-not (Test-Path $app)) "no install folder ($app)"
    Assert (-not (Test-Path $appData)) "no app data ($appData)"
    Assert (-not (Test-Path $arpKey)) 'no entry in Installed apps'
    $left = @(MenuKeys)
    Assert ($left.Count -eq 0) "no menu keys left ($(($left | ForEach-Object { $_.PSChildName }) -join ', '))"
    $added = @(AssociationNames | Where-Object { $associationsBefore -notcontains $_ })
    Assert ($added.Count -eq 0) "no SystemFileAssociations keys left behind ($($added -join ', '))"
    Assert (-not (Test-Path $classicKey) -or $userClassicKey) 'no classic menu key left'
}

function UpdateCheckEnabled {
    $state = Get-Content "$appData\update\state.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
    return [bool]($state -and $state.enabled)
}

function RunFlevertHeadless([string[]]$Arguments, [switch]$NoWait) {
    $env:FLEVERT_HEADLESS = '1'
    try { return Start-Process "$app\flevert.exe" -ArgumentList $Arguments -PassThru -WindowStyle Hidden -Wait:(-not $NoWait) }
    finally { Remove-Item Env:\FLEVERT_HEADLESS }
}

function InstalledFiles {
    @(Get-ChildItem -LiteralPath $app -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($app.Length + 1) } | Sort-Object)
}

# The command lines of every menu item for one file type, e.g. '.pdf'.
function MenuCommands([string]$Extension) {
    @(Get-ChildItem "$classes\Flevert.Menu$Extension" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -eq 'command' } | ForEach-Object { $_.GetValue('') })
}

# The installed qpdf.exe, with the empty OpenSSL configuration flevert.exe gives it (ADR-0008).
function Qpdf([string[]]$Arguments) {
    $env:OPENSSL_CONF = "$app\openssl.cnf"
    try {
        $output = & "$app\qpdf.exe" @Arguments
        # 3: done, with warnings.
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3) { throw "qpdf $Arguments failed ($LASTEXITCODE)" }
        return $output
    }
    finally { Remove-Item Env:\OPENSSL_CONF }
}

# Each page's own /Rotate, in page order (0 when it has none).
function PageRotations([string]$Pdf) {
    $json = (Qpdf @('--json=2', '--json-key=pages', '--json-key=qpdf', $Pdf)) -join "`n" | ConvertFrom-Json
    $objects = $json.qpdf[1]
    foreach ($page in $json.pages) {
        $rotate = $objects.PSObject.Properties["obj:$($page.object)"].Value.value.PSObject.Properties['/Rotate']
        if ($rotate) { [int]$rotate.Value } else { 0 }
    }
}

function AssertImage([string]$Path, [Drawing.Imaging.ImageFormat]$Format, [string]$Message) {
    $image = [Drawing.Image]::FromFile($Path)
    try { Assert ($image.RawFormat.Equals($Format)) $Message }
    finally { $image.Dispose() }
}

# A second of a 440 Hz tone, 8 kHz mono 8-bit PCM: no media file is kept in the repository.
function WriteToneWav([string]$Path) {
    $rate = 8000
    $samples = [byte[]]::new($rate)
    for ($i = 0; $i -lt $rate; $i++) { $samples[$i] = [byte](128 + 100 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate)) }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($Path))
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36 + $samples.Length))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
        $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]$rate); $writer.Write([int]$rate)
        $writer.Write([int16]1); $writer.Write([int16]8)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$samples.Length); $writer.Write($samples)
    }
    finally { $writer.Dispose() }
}

# Real conversions with the installed flevert.exe, in a new folder: a picture (ImageMagick), a PDF rotated (qpdf) and
# rendered (pdfium), two pictures combined into a PDF, and a sound converted (ffmpeg).
function AssertConversions([string]$Folder) {
    New-Item -ItemType Directory $Folder | Out-Null
    Copy-Item $heic "$Folder\photo.heic"
    $code = (RunFlevertHeadless @('run', 'image.to-jpg', "`"$Folder\photo.heic`"")).ExitCode
    Assert ($code -eq 0) "installed flevert.exe converts HEIC to JPG (exit $code)"
    $jpg = [Drawing.Image]::FromFile("$Folder\photo.jpg")
    try { Assert ($jpg.RawFormat.Equals([Drawing.Imaging.ImageFormat]::Jpeg) -and $jpg.Width -eq 320 -and $jpg.Height -eq 240) 'photo.jpg is a 320x240 JPEG' }
    finally { $jpg.Dispose() }

    Copy-Item $rotationsPdf "$Folder\rotations.pdf"
    $code = (RunFlevertHeadless @('run', 'pdf.rotate-right', "`"$Folder\rotations.pdf`"")).ExitCode
    Assert ($code -eq 0) "installed flevert.exe rotates a PDF with the bundled qpdf (exit $code)"
    # The fixture's pages are turned 0, 90, 180 and 270 degrees.
    $rotations = @(PageRotations "$Folder\rotations (rotated).pdf") -join ','
    Assert ($rotations -eq '90,180,270,0') "every page is turned a further 90 degrees right (/Rotate $rotations)"

    $code = (RunFlevertHeadless @('run', 'pdf.to-png', "`"$Folder\rotations.pdf`"")).ExitCode
    Assert ($code -eq 0) "installed flevert.exe renders PDF pages with the bundled pdfium (exit $code)"
    $pages = @(Get-ChildItem -LiteralPath "$Folder\rotations (pages)" -Filter *.png -ErrorAction SilentlyContinue)
    Assert ($pages.Count -eq 4) "one PNG per page ($($pages.Count) of 4)"
    foreach ($page in $pages) { AssertImage $page.FullName ([Drawing.Imaging.ImageFormat]::Png) "$($page.Name) is a PNG" }

    New-Item -ItemType Directory "$Folder\combine" | Out-Null
    Copy-Item "$Folder\photo.jpg" "$Folder\combine\a.jpg"
    Copy-Item $pages[0].FullName "$Folder\combine\b.png"
    $code = (RunFlevertHeadless @('run', 'pdf.from-images', "`"$Folder\combine\a.jpg`"", "`"$Folder\combine\b.png`"")).ExitCode
    Assert ($code -eq 0) "installed flevert.exe combines two pictures into a PDF (exit $code)"
    $pageCount = Qpdf @('--show-npages', "$Folder\combine\a (+1).pdf")
    Assert ("$pageCount" -eq '2') "the PDF has one page per picture ($pageCount)"

    WriteToneWav "$Folder\tone.wav"
    $code = (RunFlevertHeadless @('run', 'audio.to-mp3', "`"$Folder\tone.wav`"")).ExitCode
    Assert ($code -eq 0) "installed flevert.exe converts WAV to MP3 with the bundled ffmpeg (exit $code)"
    $codec = & "$app\ffmpeg\ffprobe.exe" -v error -show_entries stream=codec_name -of csv=p=0 "$Folder\tone.mp3"
    Assert ("$codec" -eq 'mp3') "tone.mp3 holds MP3 sound ($codec)"
}

if (-not $Setup) {
    $Setup = Get-ChildItem (Join-Path $root 'artifacts\installer\Flevert-Setup-*.exe') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'No installer found. Run scripts\build-installer.ps1 first.' }
if (Test-Path $arpKey) { throw 'Flevert is installed on this machine; uninstall it before running this test.' }
if (Test-Path $app) { throw "$app exists; remove it before running this test." }
if (Test-Path $appData) { throw "$appData exists (logs of a development build?); remove it before running this test." }
if (@(MenuKeys).Count -gt 0) { throw 'A Flevert menu is registered (flevert.exe install-menu?); run flevert.exe uninstall-menu first.' }
if (Test-Path $classicKey) { throw "The classic-menu key exists already ($classicKey); this test needs to create and remove it." }
if (@(RunningFlevert).Count -gt 0) { throw 'flevert.exe is running; a running batch leader would take over the conversions this test checks.' }
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) { throw 'Run this test without administrator rights: it checks that setup needs none.' }

$associationsBefore = AssociationNames
$userClassicKey = $false
$work = Join-Path $env:TEMP "flevert-installer-test-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
Write-Host "Testing $Setup"
try {
    # 0.1.0 was 42.6 MB and 0.2.0 47.3 MB (pdfium, qpdf and its runtime); ffmpeg adds about 46 MB (its folder packed
    # with LZMA2, measured 2026-10-20). A jump past the limit means something unexpected is packed (docs/specs/installer.md,
    # Success Criteria 1); docs/specs/media.md allows 150 MB at most.
    $size = (Get-Item $Setup).Length / 1MB
    Assert ($size -le 110) ("the setup file is at most 110 MB ({0:N1} MB)" -f $size)

    Write-Host 'Scenario 1: install, convert, install again, uninstall'
    $run = Install '1'
    Assert ($run.Seconds -lt 30) 'installs in under 30 s'
    $log = Get-Content $run.Log
    Assert (@($log | Where-Object { $_.Contains('Administrative install mode: No') }).Count -gt 0) 'installed without administrator rights'
    # Inno logs each file it unpacks to run flevert.exe check-location from %TEMP% before copying anything.
    $unpacked = @($log | Where-Object { $_.Contains('Extracting temporary file: ') } | ForEach-Object { ($_ -split '\\')[-1] })
    Assert ($unpacked -contains 'flevert.exe') 'setup unpacked flevert.exe to check the install folder'
    $engines = @($unpacked | Where-Object { $name = $_; @($conversionOnly | Where-Object { $name -like $_ }).Count -gt 0 })
    Assert ($engines.Count -eq 0) "setup did not unpack what only a conversion loads ($($engines -join ', '))"
    $freshFiles = InstalledFiles
    $freshExe = (Get-FileHash "$app\flevert.exe").Hash
    Assert (Test-Path "$app\flevert.exe") 'flevert.exe is installed'
    Assert (Test-Path "$app\LICENSE.txt") 'LICENSE.txt is installed'
    foreach ($file in $bundledFiles + 'openssl.cnf') { Assert (Test-Path -LiteralPath "$app\$file") "$file is installed" }
    # The build scripts\deps.json pins, and no other: its version line names the release and the commit.
    $pinnedFfmpeg = ([IO.File]::ReadAllText((Join-Path $root 'scripts\deps.json')) | ConvertFrom-Json).ffmpegSource.ffmpegVersion
    $ffmpegVersion = @(& "$app\ffmpeg\ffmpeg.exe" -hide_banner -version)[0]
    Assert ("$ffmpegVersion".StartsWith("ffmpeg version $pinnedFfmpeg-")) "the installed ffmpeg is the pinned build $pinnedFfmpeg ($ffmpegVersion)"
    $settings = @(Get-Content "$app\openssl.cnf" | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') })
    Assert ($settings.Count -eq 0) 'the installed openssl.cnf holds no setting, so OpenSSL in qpdf loads nothing'
    $notices = Get-Content "$app\THIRD-PARTY-NOTICES.txt" -Raw -ErrorAction SilentlyContinue
    Assert ($notices -and $notices.Contains('ImageMagick License') -and $notices.Contains('.NET Foundation')) 'third-party notices cover ImageMagick and .NET'
    # write-notices.ps1 heads each licence "<title> - <file>": one for every package and runtime pack the build holds,
    # and one for every component from deps\.
    $libraries = @(([IO.File]::ReadAllText("$app\flevert.deps.json") | ConvertFrom-Json).libraries.PSObject.Properties |
        Where-Object { $_.Value.type -ne 'project' } | ForEach-Object { ($_.Name -replace '^runtimepack\.', '') -replace '/', ' ' })
    $titles = @($libraries) + @($bundled | ForEach-Object { $_.title })
    $unlisted = @($titles | Where-Object { -not $notices.Contains("$_ - ") })
    Assert ($unlisted.Count -eq 0) "third-party notices list every bundled component ($($titles.Count); missing: $($unlisted -join ', '))"
    Assert (@(Get-ChildItem $app -Recurse -Filter *.pdb).Count -eq 0) 'no debug symbols shipped'
    Assert (Test-Path $arpKey) 'listed in Installed apps'
    $command = (Get-ChildItem "$classes\Flevert.Menu.heic" -Recurse |
        Where-Object { $_.PSChildName -eq 'command' } | Select-Object -First 1).GetValue('')
    Assert ($command -like "`"$app\flevert.exe`" run *") "the .heic menu runs the installed flevert.exe ($command)"
    Assert (Test-Path "$associations\.jpg\shell\Flevert") 'menu entry for .jpg'
    Assert ((Get-ItemProperty $classicKey).Flevert -eq '1') 'classic menu key created and marked'
    Assert (UpdateCheckEnabled) 'the check for new versions is on by default'
    Assert (@(MenuCommands '.pdf' | Where-Object { $_ -like '* run pdf.rotate-right *' }).Count -eq 1) 'the .pdf menu rotates PDFs'
    Assert (@(MenuCommands '.jpg' | Where-Object { $_ -like '* run pdf.from-images *' }).Count -eq 1) 'the .jpg menu combines pictures into a PDF'
    Assert ((Get-ItemProperty "$associations\.pdf\shell\Flevert.Tools" -ErrorAction SilentlyContinue).MUIVerb -eq 'Flevert: more PDF tools') 'PDFs have the second menu, Flevert: more PDF tools'
    # Shown only with Shift + right-click (1.2.3): the usual menu has one Flevert line.
    Assert ($null -ne (Get-ItemProperty "$associations\.pdf\shell\Flevert.Tools").PSObject.Properties['Extended']) 'the second PDF menu appears only with Shift'
    Assert ($null -eq (Get-ItemProperty "$associations\.pdf\shell\Flevert").PSObject.Properties['Extended']) 'the Flevert menu appears without Shift'
    Assert (@(MenuCommands '.pdf.tools' | Where-Object { $_ -like '* run pdf.remove-blank-pages *' }).Count -eq 1) 'Flevert: more PDF tools removes blank pages'
    # Every entry at the top of both PDF menus, and of the video and audio menus, has an icon, installed beside flevert.exe
    # (docs/specs/pdf.md, v0.2.2; docs/specs/media.md, "Menu").
    $icons = @(Get-ChildItem "$classes\Flevert.Menu.pdf\shell", "$classes\Flevert.Menu.pdf.tools\shell", "$classes\Flevert.Menu.mp4\shell", "$classes\Flevert.Menu.mp3\shell" | ForEach-Object { $_.GetValue('Icon') })
    Assert ($icons.Count -gt 0 -and @($icons | Where-Object { -not ($_ -and (Test-Path -LiteralPath ($_ -replace '^"(.*)",0$', '$1'))) }).Count -eq 0) "every entry of the .pdf, .mp4 and .mp3 menus shows an icon from $app\icons ($($icons.Count))"
    Assert (@(MenuCommands '.mp4' | Where-Object { $_ -like '* run video.compress-under-25mb *' }).Count -eq 1) 'the .mp4 menu compresses a video under 25 MB'
    $menuCount = @(MenuKeys).Count

    AssertConversions "$work\fresh"
    Assert (Test-Path "$appData\logs") 'the conversion wrote its log under app data'
    $unknown = @(Get-ChildItem $appData -Force | Where-Object { $appDataFolders -notcontains $_.Name } | ForEach-Object { $_.Name })
    Assert ($unknown.Count -eq 0) "app data holds only folders the uninstaller deletes ($($unknown -join ', '))"

    Install | Out-Null
    Assert (@(MenuKeys).Count -eq $menuCount) 'installing again leaves the same menu'
    Assert (Test-Path $classicKey) 'installing again without /CLASSICMENU keeps the earlier choice'
    Install '0' | Out-Null
    Assert (-not (Test-Path $classicKey)) 'installing again with /CLASSICMENU=0 removes the classic menu key'
    Install -UpdateCheck '0' | Out-Null
    Assert (-not (UpdateCheckEnabled)) 'installing with /UPDATECHECK=0 turns the check for new versions off'
    Install | Out-Null
    Assert (-not (UpdateCheckEnabled)) 'installing again without /UPDATECHECK keeps it off'
    Install -UpdateCheck '1' | Out-Null
    Assert (UpdateCheckEnabled) 'installing with /UPDATECHECK=1 turns it back on'
    $off = Start-Process "$app\flevert.exe" -ArgumentList 'update-check', 'off' -PassThru -Wait -WindowStyle Hidden
    Assert ($off.ExitCode -eq 0 -and -not (UpdateCheckEnabled)) 'flevert.exe update-check off turns it off'
    Install | Out-Null
    Assert (-not (UpdateCheckEnabled)) 'installing again without /UPDATECHECK keeps a check turned off with the command'
    Install -UpdateCheck 'yes' | Out-Null
    Assert (-not (UpdateCheckEnabled)) 'an /UPDATECHECK value other than 0 or 1 changes nothing'
    Install -UpdateCheck '1' | Out-Null

    Uninstall
    AssertClean

    Write-Host 'Scenario 2: the registry is cleaned even when flevert.exe cannot run'
    Install '1' | Out-Null
    [IO.File]::WriteAllBytes("$app\flevert.exe", [byte[]](1..64))
    Uninstall
    AssertClean

    Write-Host 'Scenario 3: a classic menu key the user created survives'
    New-Item "$classicKey\InprocServer32" -Force | Out-Null
    Set-ItemProperty "$classicKey\InprocServer32" -Name '(default)' -Value ''
    $userClassicKey = $true
    Install '1' | Out-Null
    Assert ($null -eq (Get-ItemProperty $classicKey).PSObject.Properties['Flevert']) 'user key left unmarked'
    Uninstall
    AssertClean
    Assert (Test-Path "$classicKey\InprocServer32") "user's classic menu key kept"
    Remove-Item $classicKey -Recurse -Force
    $userClassicKey = $false

    Write-Host 'Scenario 4: an install folder other accounts can change is refused before anything is copied or run'
    New-Item -ItemType Directory $app | Out-Null
    $acl = Get-Acl $app
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl $app $acl
    $run = Setup @('/CLASSICMENU=1')
    SetupRefused $run 'other accounts can modify it'
    Assert ((Get-Content $run.Log -Raw).Contains('#setup-refused-the-install-folder')) 'the message points to the README section for a refused folder'
    Assert (@(Get-ChildItem $app -Force).Count -eq 0) 'nothing copied into the folder'
    Assert (-not (Test-Path $arpKey)) 'nothing listed in Installed apps'
    Assert (@(MenuKeys).Count -eq 0) 'no menu registered'
    Assert (-not (Test-Path $classicKey)) 'no classic menu switched on'
    Remove-Item $app -Recurse -Force
    AssertClean

    Write-Host 'Scenario 5: /DIR cannot move the install elsewhere'
    $elsewhere = Join-Path $work 'elsewhere'
    $run = Setup @("/DIR=`"$elsewhere`"")
    SetupRefused $run 'installs only into'
    Assert (-not (Test-Path "$elsewhere\flevert.exe")) 'nothing installed there'
    AssertClean

    Write-Host 'Scenario 6: uninstalling while flevert.exe runs is cancelled, never left half done'
    Install '0' | Out-Null
    $menuCount = @(MenuKeys).Count
    # About 20 s of work (one second per file): long enough for a slow first start and the uninstaller's hand-over.
    $inputs = 1..20 | ForEach-Object { $f = "$work\slow$_.txt"; Set-Content $f 'x'; "`"$f`"" }
    $job = RunFlevertHeadless (@('run', 'dev.slow-copy') + $inputs) -NoWait
    # The uninstaller only sees flevert.exe once it holds the mutex; a fresh binary can take a while to start.
    $mutex = $null
    $deadline = (Get-Date).AddSeconds(15)
    while (-not $job.HasExited -and -not [Threading.Mutex]::TryOpenExisting('Flevert.App', [ref]$mutex) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    Assert ($null -ne $mutex) 'flevert.exe is running and holds its mutex'
    $mutex.Dispose()
    Uninstall
    Assert (-not $job.HasExited) 'flevert.exe was still running when the uninstaller finished, so the test is meaningful'
    # A silent uninstall answers the "still running" question with Cancel, so everything must still be there.
    Assert (Test-Path $arpKey) 'still listed in Installed apps'
    Assert (Test-Path "$app\flevert.exe") 'flevert.exe still installed'
    Assert (@(MenuKeys).Count -eq $menuCount) 'menu still registered'
    $job.WaitForExit()
    Uninstall
    AssertClean

    Write-Host 'Scenario 7: an install folder that is a junction is refused before anything is copied'
    $target = Join-Path $work 'junction-target'
    New-Item -ItemType Directory $target | Out-Null
    New-Item -ItemType Junction -Path $app -Target $target | Out-Null
    $run = Setup @('/CLASSICMENU=0')
    SetupRefused $run 'is a link'
    Assert (@(Get-ChildItem $target).Count -eq 0) 'nothing copied through the junction'
    (Get-Item $app).Delete()
    AssertClean

    Write-Host 'Scenario 8: a folder that already holds files from something else is refused'
    New-Item -ItemType Directory $app | Out-Null
    Set-Content "$app\planted.dll" 'not ours'
    $run = Setup @('/CLASSICMENU=0')
    SetupRefused $run 'already holds files'
    Assert (-not (Test-Path "$app\flevert.exe")) 'nothing installed beside them'
    Assert ((Get-Content "$app\planted.dll") -eq 'not ours') 'their files are left alone'
    Remove-Item $app -Recurse -Force
    AssertClean

    Write-Host 'Scenario 9: setup started from a folder that is a junction still installs'
    # A smoke test only: setup does not keep the caller's working directory, so this passes with or without the
    # drive-root fix in LinkOnPath. InstallerScriptTests checks that fix in the script itself.
    $cwdTarget = Join-Path $work 'cwd-target'
    $cwdLink = Join-Path $work 'cwd-link'
    New-Item -ItemType Directory $cwdTarget | Out-Null
    New-Item -ItemType Junction -Path $cwdLink -Target $cwdTarget | Out-Null
    $run = Setup @('/CLASSICMENU=0') -WorkingDirectory $cwdLink
    Assert ($run.ExitCode -eq 0) "setup exits with 0 (got $($run.ExitCode); log $($run.Log))"
    Uninstall
    (Get-Item $cwdLink).Delete()
    AssertClean

    # The first release has no previous one; get-previous-setup.ps1 says when one is pinned.
    if (-not (& (Join-Path $PSScriptRoot 'get-previous-setup.ps1') -IsPinned)) {
        Write-Host 'Scenario 10 skipped: no previous release is pinned in get-previous-setup.ps1 yet'
    }
    else {
        Write-Host 'Scenario 10: an upgrade over the previous release keeps both choices, adds the new menu, leaves no stale file'
        # Downloaded (or copied from -PreviousSetup) into a new private folder and checked against the pinned SHA256 first.
        $getPrevious = @{ Destination = (Join-Path $work 'previous') }
        if ($PreviousSetup) { $getPrevious.Path = $PreviousSetup }
        $previous = & (Join-Path $PSScriptRoot 'get-previous-setup.ps1') @getPrevious
        $run = Setup @('/CLASSICMENU=1', '/UPDATECHECK=0') -Exe $previous
        Assert ($run.ExitCode -eq 0) "the previous release installs (got $($run.ExitCode); log $($run.Log))"
        Write-Host "      installed $((Get-Item "$app\flevert.exe").VersionInfo.ProductVersion) from $previous"
        Assert ((Get-ItemProperty $classicKey).Flevert -eq '1') 'the previous release switched the classic menu on'
        Assert (-not (UpdateCheckEnabled)) 'the previous release turned the check for new versions off'
        $previousFiles = InstalledFiles
        Copy-Item $heic "$work\before-upgrade.heic"
        $code = (RunFlevertHeadless @('run', 'image.to-jpg', "`"$work\before-upgrade.heic`"")).ExitCode
        Assert ($code -eq 0 -and (Test-Path "$work\before-upgrade.jpg")) "the previous release converts a picture (exit $code)"

        Install | Out-Null
        Assert ((Get-FileHash "$app\flevert.exe").Hash -eq $freshExe) 'flevert.exe is the new version'
        Assert ((Get-ItemProperty $classicKey -ErrorAction SilentlyContinue).Flevert -eq '1') 'upgrading without /CLASSICMENU keeps the classic menu'
        Assert (-not (UpdateCheckEnabled)) 'upgrading without /UPDATECHECK keeps the check for new versions off'
        Assert (Test-Path "$associations\.pdf\shell\Flevert") 'the menu now has an entry for .pdf'
        Assert (@(MenuCommands '.pdf' | Where-Object { $_ -like '* run pdf.rotate-right *' }).Count -eq 1) 'the .pdf menu rotates PDFs'
        Assert (@(MenuCommands '.jpg' | Where-Object { $_ -like '* run pdf.from-images *' }).Count -eq 1) 'the .jpg menu combines pictures into a PDF'
        # Inno never deletes what an earlier version installed: anything a fresh install lacks is left over (Flevert.iss,
        # [InstallDelete]).
        $upgraded = InstalledFiles
        $stale = @($upgraded | Where-Object { $freshFiles -notcontains $_ })
        Assert ($stale.Count -eq 0) "no file of the previous release that this one does not ship is left ($($stale -join ', '))"
        $missing = @($freshFiles | Where-Object { $upgraded -notcontains $_ })
        Assert ($missing.Count -eq 0) "every file of a fresh install is there ($($missing -join ', '))"
        Write-Host "      the previous release had $($previousFiles.Count) files, this one $($upgraded.Count)"
        AssertConversions "$work\upgraded"
        Uninstall
        AssertClean
    }

    Write-Host 'PASS'
}
finally {
    WaitForUninstaller
    StopInstalledFlevert
    try {
        if (Test-Path "$app\unins000.exe") { Uninstall }
        if (Test-Path $classicKey) { Remove-Item $classicKey -Recurse -Force }
        $item = Get-Item $app -Force -ErrorAction SilentlyContinue
        # Delete a junction itself, never what it points to.
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $item.Delete() }
        elseif ($item) { Remove-Item $app -Recurse -Force }
    }
    catch { Write-Warning "cleanup failed, check $app and $classicKey by hand: $_" }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
