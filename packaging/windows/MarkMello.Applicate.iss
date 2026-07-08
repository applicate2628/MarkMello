#define MyAppName "MarkMello Applicate"
#define MyAppExeName "MarkMello.Applicate.exe"
#define MyAppProgId "Applicate.MarkMello.Markdown"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#ifndef MyPublishDir
  #define MyPublishDir "..\..\publish\win-x64"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif

#ifndef MyArchSuffix
  #define MyArchSuffix "win-x64"
#endif

#ifndef MyOutputBaseName
  #define MyOutputBaseName "MarkMello.Applicate-setup-win-x64"
#endif

#ifndef MySetupIconFile
  #define MySetupIconFile ".\markmello-installer.ico"
#endif

#ifndef MyArchitecturesAllowed
  #define MyArchitecturesAllowed "x64compatible"
#endif

#ifndef MyArchitecturesInstallMode
  #define MyArchitecturesInstallMode "x64compatible"
#endif

#ifndef MyAppId
  #define MyAppId "{{C7D9B8D3-E8A4-4C4C-94F7-0C04D60C1870}"
#endif

#ifndef MyReleaseOwner
  #define MyReleaseOwner "applicate2628"
#endif

#ifndef MyReleaseRepo
  #define MyReleaseRepo "MarkMello"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Applicate MarkMello fork contributors
AppPublisherURL=https://github.com/{#MyReleaseOwner}/{#MyReleaseRepo}
AppUpdatesURL=https://github.com/{#MyReleaseOwner}/{#MyReleaseRepo}/releases/latest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#MyArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#MyArchitecturesInstallMode}
ChangesAssociations=yes
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UsePreviousAppDir=yes
; In-place update: ask the running primary instance to close through the app's
; single-instance pipe in [Code] (PrepareToInstall), then relaunch it from [Run]
; when it was running. For a TRANSITION update from a build that predates the
; --shutdown pipe verb, PrepareToInstall does NOT abort - it falls through to
; CloseApplications=yes, whose Restart-Manager close page closes the running app
; gracefully (WM_CLOSE -> the same dirty-save prompt). RestartApplications is
; disabled explicitly (default is yes): this app never calls
; RegisterApplicationRestart, so RM cannot relaunch it - the [Run] entry owns
; relaunch.
CloseApplications=yes
RestartApplications=no
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseName}
SetupIconFile={#MySetupIconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Signing is expected to be injected by the release pipeline.
; Example:
; SignTool=signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /a $f

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register the Applicate fork as an available Markdown handler without forcing the default app.
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}"; ValueType: string; ValueName: ""; ValueData: "Markdown document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Markdown document"
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#MyAppProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Code]
const
  ApplicateMutexName = 'MarkMello.Applicate.SingleInstance';
  ApplicateShutdownArg = '--shutdown';
  ApplicateShutdownPollMilliseconds = 250;
  ApplicateShutdownTimeoutMilliseconds = 30000;
  ApplicateCloseRetryMessage = 'Please save and close MarkMello, then retry.';

var
  ApplicateRelaunchAfterInstall: Boolean;

function IsApplicateRunning: Boolean;
begin
  Result := CheckForMutexes(ApplicateMutexName);
end;

function WaitForApplicateToExit(TimeoutMilliseconds: Integer): Boolean;
var
  WaitedMilliseconds: Integer;
begin
  WaitedMilliseconds := 0;
  while IsApplicateRunning and (WaitedMilliseconds < TimeoutMilliseconds) do
  begin
    Sleep(ApplicateShutdownPollMilliseconds);
    WaitedMilliseconds := WaitedMilliseconds + ApplicateShutdownPollMilliseconds;
  end;

  Result := not IsApplicateRunning;
end;

function InitializeUninstall: Boolean;
begin
  if IsApplicateRunning then
  begin
    if not UninstallSilent then
    begin
      MsgBox(ApplicateCloseRetryMessage, mbError, MB_OK);
    end;
    Result := False;
    exit;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  ApplicateRelaunchAfterInstall := False;

  if not IsApplicateRunning then
  begin
    exit;
  end;

  { The app is running: relaunch it after install regardless of HOW it closes -
    our graceful --shutdown below, or Inno's CloseApplications / Restart-Manager
    fallback - so a running instance is always restored. }
  ApplicateRelaunchAfterInstall := True;

  { Ask the running instance to close gracefully over the single-instance pipe.
    This ONLY takes effect when the installed exe already understands --shutdown
    (installed by this or a newer build). A transition update from an OLDER build
    ignores --shutdown, so we must NOT abort on timeout: fall through and let
    CloseApplications=yes drive Inno's Restart-Manager close page - also a
    graceful WM_CLOSE that hits the same dirty-save prompt. Never force-close.
    ResultCode = 0 confirms the forward launcher succeeded before we wait. }
  if Exec(
       ExpandConstant('{app}\{#MyAppExeName}'),
       ApplicateShutdownArg,
       ExpandConstant('{app}'),
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode)
     and (ResultCode = 0) then
  begin
    WaitForApplicateToExit(ApplicateShutdownTimeoutMilliseconds);
  end;

  { Return '' unconditionally - never hard-abort the update. If the app is still
    running (transition build, hung primary, or an unattended dirty prompt), the
    Restart-Manager close page handles it next. }
end;

function ShouldRelaunchApplicate: Boolean;
begin
  Result := ApplicateRelaunchAfterInstall;
end;

function ShouldShowFreshLaunchCheckbox: Boolean;
begin
  Result := not ApplicateRelaunchAfterInstall;
end;

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunchApplicate
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent; Check: ShouldShowFreshLaunchCheckbox
