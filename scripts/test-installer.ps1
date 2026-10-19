<#
.SYNOPSIS
End-to-end test of the installer on this machine: installs silently without admin, checks files, menu and a real
conversion, installs over itself, uninstalls and checks that nothing Flevert wrote is left. Further scenarios
cover the uninstall safety net, a classic-menu key the user created, a refused install folder, /DIR, uninstalling
while flevert.exe runs, an install folder that is a junction, one that already holds someone else's files, and setup
started from a junction.

Uses the real per-user install location and HKCU, so it refuses to run when anything of Flevert is present.

.PARAMETER Setup
The setup executable; defaults to the newest artifacts\installer\Flevert-Setup-*.exe.
#>
param([string]$Setup)

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

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "  ok  $Message"
}

function MenuKeys {
    # Every key named after the app, not only the ones today's code writes, so a new kind of key cannot be left behind.
    @(Get-ChildItem $associations -ErrorAction SilentlyContinue |
        Where-Object { Test-Path "$($_.PSPath)\shell\Flevert" }) +
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

function Setup([string[]]$Arguments, [string]$WorkingDirectory = $work) {
    $log = Join-Path $work "setup-$([guid]::NewGuid()).log"
    $all = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $Arguments
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Setup -ArgumentList $all -WorkingDirectory $WorkingDirectory -Wait -PassThru
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Log = $log; Seconds = $timer.Elapsed.TotalSeconds }
}

function Install([string]$ClassicMenu = '') {
    $arguments = @()
    if ($ClassicMenu) { $arguments += "/CLASSICMENU=$ClassicMenu" }
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

function RunFlevertHeadless([string[]]$Arguments, [switch]$NoWait) {
    $env:FLEVERT_HEADLESS = '1'
    try { return Start-Process "$app\flevert.exe" -ArgumentList $Arguments -PassThru -WindowStyle Hidden -Wait:(-not $NoWait) }
    finally { Remove-Item Env:\FLEVERT_HEADLESS }
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
    $size = (Get-Item $Setup).Length / 1MB
    Assert ($size -le 60) ("the setup file is at most 60 MB ({0:N1} MB)" -f $size)

    Write-Host 'Scenario 1: install, convert, install again, uninstall'
    $run = Install '1'
    Assert ($run.Seconds -lt 30) 'installs in under 30 s'
    Assert ((Get-Content $run.Log -Raw).Contains('Administrative install mode: No')) 'installed without administrator rights'
    Assert (Test-Path "$app\flevert.exe") 'flevert.exe is installed'
    Assert (Test-Path "$app\LICENSE.txt") 'LICENSE.txt is installed'
    $notices = Get-Content "$app\THIRD-PARTY-NOTICES.txt" -Raw -ErrorAction SilentlyContinue
    Assert ($notices -and $notices.Contains('ImageMagick License') -and $notices.Contains('.NET Foundation')) 'third-party notices cover ImageMagick and .NET'
    Assert (@(Get-ChildItem $app -Recurse -Filter *.pdb).Count -eq 0) 'no debug symbols shipped'
    Assert (Test-Path $arpKey) 'listed in Installed apps'
    $command = (Get-ChildItem "$classes\Flevert.Menu.heic" -Recurse |
        Where-Object { $_.PSChildName -eq 'command' } | Select-Object -First 1).GetValue('')
    Assert ($command -like "`"$app\flevert.exe`" run *") "the .heic menu runs the installed flevert.exe ($command)"
    Assert (Test-Path "$associations\.jpg\shell\Flevert") 'menu entry for .jpg'
    Assert ((Get-ItemProperty $classicKey).Flevert -eq '1') 'classic menu key created and marked'
    $menuCount = @(MenuKeys).Count

    Copy-Item $heic "$work\photo.heic"
    $code = (RunFlevertHeadless @('run', 'image.to-jpg', "`"$work\photo.heic`"")).ExitCode
    Assert ($code -eq 0) "installed flevert.exe converts HEIC to JPG (exit $code)"
    $jpg = [Drawing.Image]::FromFile("$work\photo.jpg")
    try { Assert ($jpg.RawFormat.Equals([Drawing.Imaging.ImageFormat]::Jpeg) -and $jpg.Width -eq 320 -and $jpg.Height -eq 240) 'photo.jpg is a 320x240 JPEG' }
    finally { $jpg.Dispose() }
    Assert (Test-Path "$appData\logs") 'the conversion wrote its log under app data'

    Install | Out-Null
    Assert (@(MenuKeys).Count -eq $menuCount) 'installing again leaves the same menu'
    Assert (Test-Path $classicKey) 'installing again without /CLASSICMENU keeps the earlier choice'
    Install '0' | Out-Null
    Assert (-not (Test-Path $classicKey)) 'installing again with /CLASSICMENU=0 removes the classic menu key'

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
