#if Ver < EncodeVer(6, 7, 3)
  #error Use Inno Setup 6.7.3 or newer.
#endif
#ifndef PayloadDir
  #error Invoke Publish-Release.ps1 to prepare the shared Windows payload.
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion must match the payload's installation manifest.
#endif

#define SetupId "8C81BC4F-02B9-4ABA-B753-43F5DAB69893"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + SetupId + "}_is1"
#define AppInstanceMutex "Local\CodexQuotaWidget.v2"

[Setup]
AppId={{{#SetupId}}
AppName=Codex Quota
AppVersion={#ReleaseVersion}
AppPublisher=IcebearHound
AppPublisherURL=https://github.com/IcebearHound/CodexQuota
AppSupportURL=https://github.com/IcebearHound/CodexQuota/issues
AppUpdatesURL=https://github.com/IcebearHound/CodexQuota/releases/latest
DefaultDirName=D:\Program Files\CodexQuota
DisableDirPage=yes
AppendDefaultDirName=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableStartupPrompt=yes
ShowLanguageDialog=no
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=no
UninstallDisplayIcon={app}\CodexQuota.exe
SetupIconFile=..\Quota.Desktop\Assets\app.ico
WizardStyle=modern dynamic windows11
OutputBaseFilename=CodexQuota-{#ReleaseVersion}-win-x64-setup
Compression=lzma2/fast
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
en.AppRunning=Quit Codex Quota from its tray menu, then try again.
zh.AppRunning=请从托盘菜单退出 Codex Quota 后重试。
en.StartFailed=Could not open the Codex Quota installer.
zh.StartFailed=未能打开 Codex Quota 安装界面。
en.FolderOccupied=Choose a new or empty folder. Setup will not overwrite an unrelated or portable installation.
zh.FolderOccupied=请选择新建目录或空目录。安装程序不会覆盖其他文件或已有的便携版。
en.InstallLocationLocked=To change the location of an existing installed copy, uninstall it first. Your accounts and settings will be kept.
zh.InstallLocationLocked=如需为已安装的版本更换目录，请先卸载。账号和设置会保留。
en.NewerVersionInstalled=This folder already contains a newer version of Codex Quota.
zh.NewerVersionInstalled=此目录已安装更新版本的 Codex Quota。

[Files]
; The same executable renders the branded frontend and is installed as the app.
Source: "{#PayloadDir}\CodexQuota.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "CodexQuota.exe"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\Codex Quota"; Filename: "{app}\CodexQuota.exe"; WorkingDir: "{app}"

[Code]
const
  StartupRun = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupApproved = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

function RegisteredLocation: String;
var
  Directory: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, '{#UninstallKey}', 'InstallLocation', Directory) and
    (Directory <> '') and FileExists(AddBackslash(Directory) + 'unins000.exe') then
    Result := RemoveBackslashUnlessRoot(Directory);
end;

function InitializeSetup: Boolean;
var
  Arguments, Previous: String;
  ExitCode: Integer;
begin
  Result := WizardSilent;
  if Result then Exit;

  // The WPF frontend shares the app's resources and calls this package in silent mode.
  ExtractTemporaryFile('CodexQuota.exe');
  Previous := RegisteredLocation;
  if (Length(Previous) <> 0) and (Previous[Length(Previous)] = '\') then Previous := Previous + '\';
  Arguments := '--setup-ui "' + ExpandConstant('{srcexe}') + '" "' + Previous + '"';
  if not Exec(ExpandConstant('{tmp}\CodexQuota.exe'), Arguments, ExpandConstant('{tmp}'),
      SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
    MsgBox(CustomMessage('StartFailed'), mbError, MB_OK);
end;

function ReportFailure(const Code, MessageName: String): String;
var
  ReportPath: String;
begin
  ReportPath := ExpandConstant('{param:CQRESULT|}');
  if ReportPath <> '' then SaveStringToFile(ReportPath, Code, False);
  Result := CustomMessage(MessageName);
end;

function DirectoryHasEntries(const Directory: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := False;
  if not FindFirst(AddBackslash(Directory) + '*', Entry) then Exit;
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Destination, Registered: String;
  InstalledVersion, SetupVersion: Int64;
begin
  Result := '';
  if CheckForMutexes('{#AppInstanceMutex}') then
  begin
    Result := ReportFailure('running', 'AppRunning');
    Exit;
  end;
  Destination := AddBackslash(ExpandConstant('{app}'));
  Registered := RegisteredLocation;
  if Registered <> '' then
  begin
    if CompareText(Destination, AddBackslash(Registered)) <> 0 then
      Result := ReportFailure('location', 'InstallLocationLocked')
    else if GetPackedVersion(Destination + 'CodexQuota.exe', InstalledVersion)
      and StrToVersion('{#ReleaseVersion}', SetupVersion)
      and (ComparePackedVersion(InstalledVersion, SetupVersion) > 0) then
      Result := ReportFailure('newer', 'NewerVersionInstalled');
  end
  else if DirectoryHasEntries(Destination) then
    Result := ReportFailure('occupied', 'FolderOccupied');
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('{#AppInstanceMutex}');
  if not Result then SuppressibleMsgBox(CustomMessage('AppRunning'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Command: String;
begin
  if (Step = usPostUninstall) and RegQueryStringValue(HKCU, StartupRun, 'CodexQuota', Command)
    and (CompareText(Command, '"' + ExpandConstant('{app}\CodexQuota.exe') + '" --background') = 0) then
  begin
    RegDeleteValue(HKCU, StartupRun, 'CodexQuota');
    RegDeleteValue(HKCU, StartupApproved, 'CodexQuota');
  end;
end;
