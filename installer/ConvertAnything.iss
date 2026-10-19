; Inno Setup 6 script for ConvertAnything. Build with scripts\build-installer.ps1; spec in SPEC-installer.md.
; Per-user install without admin: the right-click menu lives in HKCU, so a machine-wide install would gain nothing.

#if VER < EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 or later is required (RedirectionGuard, random temp folder names).
#endif

#define AppName "ConvertAnything"
#define AppExe "ca.exe"
#define AppUrl "https://github.com/Suryvibegod/ConvertAnything"
; Held by every running ca.exe (AppMutex.cs).
#define AppMutexName "ConvertAnything.App"
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
; One place declares the version (Directory.Build.props); read it back from ca.exe and drop the fourth part.
#define FileVersion GetVersionNumbersString(AddBackslash(SourceDir) + AppExe)
#if FileVersion == ""
  #error ca.exe not found in SourceDir; run scripts\build-installer.ps1.
#endif
#define AppVersion Copy(FileVersion, 1, RPos(".", FileVersion) - 1)

[Setup]
AppId={{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName} contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#FileVersion}
PrivilegesRequired=lowest
; A fixed folder: ca.exe refuses to register a menu for a folder other accounts could change (InstallLocation), and
; setup runs ca.exe from it. Hiding the page is not enough, /DIR still overrides it: PrepareToInstall enforces it.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
; 64-bit mode also makes the Pascal HKCU\Software\Classes\CLSID below the same registry view the x64 ca.exe writes.
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
SetupIconFile=..\assets\app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Setup and Uninstall wait while ca.exe runs: a per-user uninstaller cannot remove files in use, and they would stay
; behind with no uninstaller left. Restart Manager covers the same case for upgrades.
AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no
; Tells Explorer the menu changed, also when only the uninstall safety net below could clean up.
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; The classic-menu choice is a check box on a custom page, not a [Tasks] entry: Inno Setup 6.7 clips the check box of the
; task list at high DPI (seen at 200%), while a plain check box is drawn by Windows. Silent installs pass /CLASSICMENU=1|0.

[Files]
Source: "{#SourceDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
; What ca.exe writes under %LOCALAPPDATA%\ConvertAnything: logs, batch keys and the ImageMagick policy. Named one by one,
; so files someone else keeps in a folder of the same name survive.
Type: filesandordirs; Name: "{localappdata}\{#AppName}\logs"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\pipes"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\magick"
Type: dirifempty; Name: "{localappdata}\{#AppName}"
; Inno removes only folders it created; this also removes an empty install folder that existed before.
Type: dirifempty; Name: "{app}"

[Code]
const
  Classes = 'Software\Classes';
  ClassicMenuKey = 'Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}';
  ClassicMenuMarker = 'ConvertAnything';
  // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION: Smart App Control or another application control policy blocked the file.
  ErrorPolicyBlocked = 4551;
  // Setup's exit code when it installed the files but could not set up the menu; Inno's own codes stop at 8.
  ExitMenuFailed = 10;

var
  ClassicMenuCheck: TNewCheckBox; // nil before Windows 11, where the classic menu is the only one
  MenuFailed: Boolean;

function IsWindows11: Boolean;
begin
  // Major 10, build 22000 or later.
  Result := GetWindowsVersion >= $0A0055F0;
end;

// Runs ca.exe and waits. False when it could not start or exited with an error. Reason holds what it wrote to stderr.
function RunCa(const Params: String; var Started: Boolean; var Code: Integer; var Reason: String): Boolean;
var
  Output: TExecOutput;
  I: Integer;
begin
  Reason := '';
  Started := ExecAndCaptureOutput(ExpandConstant('{app}\{#AppExe}'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
  if Started then
    for I := 0 to GetArrayLength(Output.StdErr) - 1 do
      Reason := Trim(Reason + ' ' + Output.StdErr[I]);
  Log(Format('ca.exe %s: started=%d code=%d %s', [Params, Ord(Started), Code, Reason]));
  Result := Started and (Code = 0);
end;

procedure ReportFailure(const What: String; Started: Boolean; Code: Integer; const Reason: String);
var
  Text: String;
begin
  MenuFailed := True;
  Text := Format('ConvertAnything was installed, but %s failed (code %d).', [What, Code]);
  if Reason <> '' then
    Text := Text + #13#10#13#10 + Reason;
  // ca.exe's own failures exit with 1-3. Anything else means it did not run: not started at all, or stopped while
  // loading, which is how Smart App Control blocking one of its DLLs shows up.
  if (not Started) or (Code = ErrorPolicyBlocked) or (Code < 1) or (Code > 3) then
    Text := Text + #13#10#13#10 + 'Windows may have blocked ConvertAnything (Smart App Control). See {#AppUrl}#smart-app-control'
  else
    Text := Text + #13#10#13#10 + 'See {#AppUrl}#if-the-menu-is-missing';
  SuppressibleMsgBox(Text, mbError, MB_OK, IDOK);
end;

procedure InitializeWizard;
var
  Page: TWizardPage;
  Explanation: TNewStaticText;
  Choice: String;
begin
  if not IsWindows11 then
    Exit;
  Page := CreateCustomPage(wpWelcome, 'Right-click menu', 'Where should ConvertAnything appear?');

  Explanation := TNewStaticText.Create(Page);
  Explanation.Parent := Page.Surface;
  Explanation.Width := Page.SurfaceWidth;
  Explanation.WordWrap := True;
  Explanation.Caption := 'Windows 11 puts ConvertAnything under "Show more options". The option below brings back ' +
    'the classic right-click menu, as in Windows 10. It applies to all apps and is undone when you uninstall ConvertAnything.';

  ClassicMenuCheck := TNewCheckBox.Create(Page);
  ClassicMenuCheck.Parent := Page.Surface;
  ClassicMenuCheck.Top := Explanation.Top + Explanation.Height + ScaleY(16);
  ClassicMenuCheck.Width := Page.SurfaceWidth;
  ClassicMenuCheck.Height := ScaleY(24);
  ClassicMenuCheck.Caption := 'Use the classic (Windows 10 style) right-click menu for all apps';

  // /CLASSICMENU=1|0 for silent installs; otherwise keep the choice of the previous install.
  Choice := ExpandConstant('{param:CLASSICMENU|}');
  if Choice = '' then
    Choice := GetPreviousData('ClassicMenu', '0');
  ClassicMenuCheck.Checked := Choice = '1';
end;

function ClassicMenuChosen: Boolean;
begin
  Result := (ClassicMenuCheck <> nil) and ClassicMenuCheck.Checked;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if ClassicMenuChosen then
    SetPreviousData(PreviousDataKey, 'ClassicMenu', '1')
  else
    SetPreviousData(PreviousDataKey, 'ClassicMenu', '0');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(RemoveBackslash(ExpandConstant('{app}')), ExpandConstant('{userpf}\{#AppName}')) <> 0 then
    Result := 'ConvertAnything installs only into ' + ExpandConstant('{userpf}\{#AppName}') +
      ': another folder may be changeable by other accounts, and the right-click menu would run their code.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Started: Boolean;
  Code: Integer;
  Reason: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if not RunCa('install-menu', Started, Code, Reason) then
    ReportFailure('adding the right-click menu', Started, Code, Reason);
  // Applied both ways, so unticking it on an upgrade undoes it; off only ever removes the key ca.exe created.
  if ClassicMenuChosen then
  begin
    if not RunCa('classic-menu on', Started, Code, Reason) then
      ReportFailure('switching to the classic menu', Started, Code, Reason);
  end
  else
    RunCa('classic-menu off', Started, Code, Reason);
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if MenuFailed then
    Result := ExitMenuFailed;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: String;
begin
  if CurPageID <> wpFinished then
    Exit;
  if MenuFailed then
    Text := 'Setup copied ConvertAnything to your computer, but could not finish setting up the right-click menu. ' +
      'See {#AppUrl}#if-the-menu-is-missing'
  else
  begin
    Text := 'Setup has finished installing ConvertAnything.' + #13#10#13#10;
    if ClassicMenuChosen then
      Text := Text + 'Right-click a picture and choose ConvertAnything. The classic menu appears after you sign out and back in, or restart your PC.'
    else if IsWindows11 then
      Text := Text + 'Right-click a picture, choose "Show more options", then ConvertAnything.'
    else
      Text := Text + 'Right-click a picture and choose ConvertAnything.';
  end;
  WizardForm.FinishedLabel.Caption := Text + #13#10#13#10 + SetupMessage(msgClickFinish);
  // The label was sized for the default text; without this longer text is cut off.
  WizardForm.FinishedLabel.AdjustHeight;
end;

// Safety net for uninstall: removes what ca.exe uninstall-menu and classic-menu off would, in case ca.exe cannot run
// (blocked, damaged or deleted). A menu left pointing at a deleted ca.exe is the worst thing to leave behind.
// Mirrors MenuInstaller.Uninstall (with WindowsRegistry's pruning of empty parents) and ClassicMenu.Off; harmless when
// they already ran. InstallerScriptTests keeps the key names in step with the C# code.
procedure SweepRegistry;
var
  Names: TArrayOfString;
  I: Integer;
  Ext: String;
begin
  if RegGetSubkeyNames(HKCU, Classes + '\SystemFileAssociations', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Ext := Classes + '\SystemFileAssociations\' + Names[I];
      if RegKeyExists(HKCU, Ext + '\shell\{#AppName}') then
      begin
        RegDeleteKeyIncludingSubkeys(HKCU, Ext + '\shell\{#AppName}');
        RegDeleteKeyIfEmpty(HKCU, Ext + '\shell');
        RegDeleteKeyIfEmpty(HKCU, Ext);
      end;
    end;

  if RegGetSubkeyNames(HKCU, Classes, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(Lowercase('{#AppName}.Menu'), Lowercase(Names[I])) = 1 then
        RegDeleteKeyIncludingSubkeys(HKCU, Classes + '\' + Names[I]);

  if RegValueExists(HKCU, ClassicMenuKey, ClassicMenuMarker) then
    RegDeleteKeyIncludingSubkeys(HKCU, ClassicMenuKey);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Started: Boolean;
  Code: Integer;
  Reason: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  RunCa('uninstall-menu', Started, Code, Reason);
  RunCa('classic-menu off', Started, Code, Reason);
  SweepRegistry;
end;
