; OpenVideoSmartShare installer (Inno Setup 7). Build it with tools\build-installer.ps1 after
; tools\publish.ps1: the version comes from the published exe.
;
; Per-user install, no admin rights, into %LOCALAPPDATA%\Programs\OpenVideoSmartShare: the
; firewall rule and the autostart entry created by the app refer to that path. Missing .NET
; runtimes are downloaded from Microsoft and installed with a single UAC prompt.

#ifndef DistDir
  #define DistDir "..\dist"
#endif

#define AppName "OpenVideoSmartShare"
#define AppExe "OpenVideoSmartShare.exe"
#define AppPublisher "Daniele Vergini"
#define AppUrl "https://github.com/wergio/OpenVideoSmartShare"
#define AppExePath AddBackslash(DistDir) + AppExe

#if !FileExists(AppExePath)
  #error "Run tools\publish.ps1 first: the published exe was not found"
#endif

; ProductVersion is the full version (e.g. 1.2.0-beta.1), FileVersion the numeric one.
#define AppVersion GetStringFileInfo(AppExePath, "ProductVersion")
#define AppNumericVersion GetVersionNumbersString(AppExePath)

; The app targets .NET 10 with RollForward=Major: any later major works too.
#define DotNetMajor "10"
#define DotNetChannel "10.0"

[Setup]
AppId={{8D8FC3C1-637A-4128-81C7-0AD9A2CC7EEF}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) 2026 {#AppPublisher}
DefaultDirName={userpf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\OpenVideoSmartShare\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The tray app is closed through its exit event (see CloseRunningApp), not Restart Manager.
CloseApplications=no
ShowLanguageDialog=auto
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppNumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (c) 2026 {#AppPublisher}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "da"; MessagesFile: "compiler:Languages\Danish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fi"; MessagesFile: "compiler:Languages\Finnish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "hu"; MessagesFile: "compiler:Languages\Hungarian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "nb"; MessagesFile: "compiler:Languages\Norwegian.isl"
Name: "nl"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "sv"; MessagesFile: "compiler:Languages\Swedish.isl"
Name: "th"; MessagesFile: "compiler:Languages\Thai.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "zhhans"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "zhhant"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[CustomMessages]
; English for every language, Italian override.
RuntimesToInstall=.NET runtimes to download and install:
InstallingRuntimes=Installing the .NET runtimes...
RuntimesFailed=The .NET runtimes could not be installed. OpenVideoSmartShare will offer to download them when it starts.
DeleteUserData=Also delete the OpenVideoSmartShare settings, log and library cache?
it.RuntimesToInstall=Runtime .NET da scaricare e installare:
it.InstallingRuntimes=Installazione dei runtime .NET...
it.RuntimesFailed=Non è stato possibile installare i runtime .NET. OpenVideoSmartShare proporrà di scaricarli all'avvio.
it.DeleteUserData=Eliminare anche impostazioni, log e cache della libreria di OpenVideoSmartShare?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppExePath}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  // Named objects created by the app (Program.cs).
  ExitEventName = 'Local\OpenVideoSmartShare.Exit';
  InstanceMutexName = 'Local\OpenVideoSmartShare.Instance';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  // Microsoft's links to the latest patch of the channel.
  DesktopRuntimeUrl = 'https://aka.ms/dotnet/{#DotNetChannel}/windowsdesktop-runtime-win-x64.exe';
  AspNetRuntimeUrl = 'https://aka.ms/dotnet/{#DotNetChannel}/aspnetcore-runtime-win-x64.exe';
  DesktopRuntimeFile = 'windowsdesktop-runtime-win-x64.exe';
  AspNetRuntimeFile = 'aspnetcore-runtime-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;
  NeedsDesktopRuntime: Boolean;
  NeedsAspNetRuntime: Boolean;

function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

// Asks a running instance (installed or portable) to quit, like "OpenVideoSmartShare.exe --exit",
// and waits for it to release its instance mutex.
procedure CloseRunningApp;
var
  Event: THandle;
  Waited: Integer;
begin
  Event := OpenEvent(EVENT_MODIFY_STATE, False, ExitEventName);
  if Event = 0 then
    Exit;
  SetEvent(Event);
  CloseHandle(Event);
  Waited := 0;
  while CheckForMutexes(InstanceMutexName) and (Waited < 10000) do
  begin
    Sleep(100);
    Waited := Waited + 100;
  end;
  Log(Format('Asked the running instance to quit, waited %d ms', [Waited]));
end;

// Root of the x64 .NET install, found the same way as the .NET host does.
function DotNetRoot: String;
begin
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Result) then
    Result := ExpandConstant('{commonpf64}\dotnet');
end;

// True when the shared framework is installed in version {#DotNetMajor} or later.
function HasFramework(Name: String): Boolean;
var
  FindRec: TFindRec;
  Major: Integer;
begin
  Result := False;
  if not FindFirst(AddBackslash(DotNetRoot) + 'shared\' + Name + '\*', FindRec) then
    Exit;
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
      begin
        Major := StrToIntDef(Copy(FindRec.Name, 1, Pos('.', FindRec.Name) - 1), 0);
        if Major >= {#DotNetMajor} then
          Result := True;
      end;
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure DetectRuntimes;
begin
  NeedsDesktopRuntime := not HasFramework('Microsoft.WindowsDesktop.App');
  NeedsAspNetRuntime := not HasFramework('Microsoft.AspNetCore.App');
  Log(Format('Missing runtimes: Desktop=%d, ASP.NET Core=%d', [Ord(NeedsDesktopRuntime), Ord(NeedsAspNetRuntime)]));
end;

function InitializeSetup: Boolean;
begin
  DetectRuntimes;
  Result := True;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoGroupInfo <> '' then
    Result := Result + NewLine + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  if NeedsDesktopRuntime or NeedsAspNetRuntime then
  begin
    Result := Result + NewLine + NewLine + CustomMessage('RuntimesToInstall');
    if NeedsDesktopRuntime then
      Result := Result + NewLine + Space + '.NET {#DotNetMajor} Desktop Runtime';
    if NeedsAspNetRuntime then
      Result := Result + NewLine + Space + 'ASP.NET Core Runtime {#DotNetMajor}';
  end;
end;

// Runs the downloaded runtime installers in one elevated command (a single UAC prompt), then
// checks again what is installed.
function InstallRuntimes: Boolean;
var
  Mode, Command: String;
  ResultCode: Integer;
begin
  if WizardSilent then
    Mode := '/quiet'
  else
    Mode := '/passive';
  Command := '';
  if NeedsDesktopRuntime then
    Command := '"' + ExpandConstant('{tmp}\') + DesktopRuntimeFile + '" /install ' + Mode + ' /norestart';
  if NeedsAspNetRuntime then
  begin
    if Command <> '' then
      Command := Command + ' & ';
    Command := Command + '"' + ExpandConstant('{tmp}\') + AspNetRuntimeFile + '" /install ' + Mode + ' /norestart';
  end;
  if ShellExec('runas', ExpandConstant('{cmd}'), '/c "' + Command + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('Runtime installers finished with exit code %d', [ResultCode]))
  else
    Log('Runtime installers not started: ' + SysErrorMessage(ResultCode));
  DetectRuntimes;
  Result := not (NeedsDesktopRuntime or NeedsAspNetRuntime);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not (NeedsDesktopRuntime or NeedsAspNetRuntime) then
    Exit;
  DownloadPage.Clear;
  if NeedsDesktopRuntime then
    DownloadPage.Add(DesktopRuntimeUrl, DesktopRuntimeFile, '');
  if NeedsAspNetRuntime then
    DownloadPage.Add(AspNetRuntimeUrl, AspNetRuntimeFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      DownloadPage.SetText(CustomMessage('InstallingRuntimes'), '');
      DownloadPage.ProgressBar.Style := npbstMarquee;
      if not InstallRuntimes then
        SuppressibleMsgBox(CustomMessage('RuntimesFailed'), mbError, MB_OK, IDOK);
    except
      if DownloadPage.AbortedByUser then
      begin
        // Back to the Ready page: the user can retry or cancel the setup.
        Log('Runtime download cancelled');
        Result := False;
      end
      else
        // Install the app anyway: without runtimes it shows Microsoft's download dialog.
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage) + #13#10#13#10 + CustomMessage('RuntimesFailed'), mbError, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp;
  Result := '';
end;

// Removes "Start with Windows" when it points to this installation (a portable copy elsewhere
// keeps its own entry).
procedure RemoveAutostart;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, '{#AppName}', Command)
    and (Pos(Uppercase(ExpandConstant('{app}\{#AppExe}')), Uppercase(Command)) > 0) then
    RegDeleteValue(HKCU, RunKey, '{#AppName}');
end;

// Removes the inbound firewall rules of the installed exe. Reading the rules needs no rights,
// deleting them does: the UAC prompt only appears when a rule exists.
procedure RemoveFirewallRules;
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not Exec(ExpandConstant('{cmd}'),
    '/c netsh advfirewall firewall show rule name=all dir=in verbose | findstr /i /l /c:"' + Exe + '" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Exit;
  if ShellExec('runas', 'netsh.exe', 'advfirewall firewall delete rule name=all dir=in program="' + Exe + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('Firewall rules removed, exit code %d', [ResultCode]))
  else
    Log('Firewall rules not removed: ' + SysErrorMessage(ResultCode));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usUninstall:
      begin
        // After the confirmation, before the files are removed.
        CloseRunningApp;
        RemoveAutostart;
        RemoveFirewallRules;
      end;
    usPostUninstall:
      if not UninstallSilent
        and (MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      begin
        DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
        DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
      end;
  end;
end;
