; Inno Setup 6 script for ConvertAnything. Build with scripts\build-installer.ps1; spec in SPEC-installer.md.
; Per-user install without admin: the right-click menu lives in HKCU, so a machine-wide install would gain nothing.

#define AppName "ConvertAnything"
#define AppExe "ca.exe"
#define AppUrl "https://github.com/Suryvibegod/ConvertAnything"
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
; One place declares the version (Directory.Build.props); read it back from ca.exe and drop the fourth part.
#define FileVersion GetVersionNumbersString(AddBackslash(SourceDir) + AppExe)
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
; A fixed folder: ca.exe refuses to register a menu for a folder other accounts could change (InstallLocation).
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Restart Manager closes a running ca.exe before files are replaced.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "classicmenu"; Description: "Use the classic right-click menu, so ConvertAnything appears without ""Show more options"" (affects all apps; undone on uninstall)"; Flags: unchecked; Check: IsWindows11

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
; Logs, batch keys and the ImageMagick policy; nothing the user made.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
const
  Classes = 'Software\Classes';
  ClassicMenuKey = 'Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}';
  ClassicMenuMarker = 'ConvertAnything';
  // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION: Smart App Control or another application control policy blocked the file.
  ErrorPolicyBlocked = 4551;

function IsWindows11: Boolean;
begin
  // Major 10, build 22000 or later.
  Result := GetWindowsVersion >= $0A0055F0;
end;

// Runs ca.exe and waits. False when it could not start or exited with an error; Code holds the reason.
function RunCa(const Params: String; var Code: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{app}\{#AppExe}'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  Log(Format('ca.exe %s -> %d', [Params, Code]));
end;

procedure ReportFailure(const What: String; Code: Integer);
var
  Text: String;
begin
  Text := Format('ConvertAnything was installed, but %s failed (code %d).', [What, Code]);
  if Code = ErrorPolicyBlocked then
    Text := Text + #13#10#13#10 + 'Windows blocked ca.exe (Smart App Control). See ' + '{#AppUrl}#install for what to do.'
  else
    Text := Text + #13#10#13#10 + 'Run setup again, or report it at {#AppUrl}/issues.';
  SuppressibleMsgBox(Text, mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if not RunCa('install-menu', Code) then
    ReportFailure('adding the right-click menu', Code);
  if WizardIsTaskSelected('classicmenu') and not RunCa('classic-menu on', Code) then
    ReportFailure('switching to the classic menu', Code);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID <> wpFinished then
    Exit;
  if WizardIsTaskSelected('classicmenu') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Right-click a picture and choose ConvertAnything. The classic menu takes effect after you sign out and back in, or restart Windows Explorer.'
  else if IsWindows11 then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Right-click a picture, choose "Show more options", then ConvertAnything.'
  else
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Right-click a picture and choose ConvertAnything.';
end;

// Safety net for uninstall: removes what ca.exe uninstall-menu and classic-menu off would, in case ca.exe cannot run
// (blocked, damaged or deleted). A menu left pointing at a deleted ca.exe is the worst thing to leave behind.
// Mirrors MenuInstaller.Uninstall and ClassicMenu.Off; harmless when they already ran.
procedure DeleteIfEmpty(const Key: String);
begin
  if RegKeyExists(HKCU, Key) then
    RegDeleteKeyIfEmpty(HKCU, Key);
end;

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
        DeleteIfEmpty(Ext + '\shell');
        DeleteIfEmpty(Ext);
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
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  RunCa('uninstall-menu', Code);
  RunCa('classic-menu off', Code);
  SweepRegistry;
end;
