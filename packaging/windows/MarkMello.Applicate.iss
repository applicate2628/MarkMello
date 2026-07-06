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
; In-place update: close the running instance so its locked files can be
; overwritten, then relaunch it after install. Restart Manager does the work
; (CloseApplications shuts it down, RestartApplications brings it back);
; AppMutex matches the app's single-instance mutex
; (ApplicateSingleInstanceService.cs:9, unqualified => Local namespace) so Setup
; can detect the running instance as an explicit fallback close prompt. The [Run]
; postinstall entry still covers the fresh-install Launch checkbox; the app's
; single-instance mutex dedupes any redundant launch.
CloseApplications=yes
RestartApplications=yes
AppMutex=MarkMello.Applicate.SingleInstance
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

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
