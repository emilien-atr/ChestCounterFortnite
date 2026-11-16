; ChestCounterFortnite installer.
; Build with: ISCC.exe /DAppVersion=1.6.0 installer\ChestCounter.iss
; The app is published self-contained in ..\dist\ChestCounterFortnite.exe first.

#define AppName "ChestCounterFortnite"
#define AppPublisher "Emilien"
#define AppExeName "ChestCounterFortnite.exe"
#ifndef AppVersion
  #define AppVersion "1.6.0"
#endif

[Setup]
; Keep this value stable: it identifies updates and the uninstall entry.
AppId={{A2F6C4E8-3B7D-4E11-9C2A-5D8F1B0E7C64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/emilien-atr/ChestCounterFortnite
AppSupportURL=https://github.com/emilien-atr/ChestCounterFortnite/issues
AppUpdatesURL=https://github.com/emilien-atr/ChestCounterFortnite/releases/latest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist-installer
OutputBaseFilename=ChestCounterFortnite-Setup
SetupIconFile=..\src\ChestCounter\Assets\ChestCounterFortnite.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0
VersionInfoDescription={#AppName} installer
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
LicenseFile=..\LICENSE
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\ChestCounterFortnite.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the "open with Fortnite" Run entry and stops the invisible standby process.
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-cleanup"; Flags: runhidden skipifdoesntexist; RunOnceId: "StopStandby"

[UninstallDelete]
Type: files; Name: "{app}\ChestCounterFortnite.exe.old"
Type: files; Name: "{app}\ChestCounterFortnite.exe.download"
