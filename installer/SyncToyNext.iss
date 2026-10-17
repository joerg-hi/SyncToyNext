; Installer für SyncToyNext (Inno Setup 6.3 oder neuer)
;
; Wird im GitHub-Workflow gebaut. Lokal:
;   iscc /DAppVersion=1.1.0 /DSourceDir=..\out\SyncToyNext-win-x64 installer\SyncToyNext.iss
;
; Standardmäßig wird ohne Administratorrechte für den aktuellen Benutzer installiert
; (%LOCALAPPDATA%\Programs\SyncToyNext). Im Setup kann auf „für alle Benutzer“ umgestellt werden.
; Die Ordnerpaare unter %LOCALAPPDATA%\SyncToyNext bleiben bei einer Deinstallation erhalten.

#define AppName "SyncToyNext"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\SyncToyNext-win-x64"
#endif

[Setup]
AppId={{B6F2C9A4-6E2D-4C55-9C4E-2F6E8A1D7B31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=joerg-hi
AppPublisherURL=https://github.com/joerg-hi/SyncToyNext
AppSupportURL=https://github.com/joerg-hi/SyncToyNext
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\out
OutputBaseFilename=SyncToyNext-Setup-{#AppVersion}
SetupIconFile=..\src\SyncToyNext.App\Assets\SyncToyNext.ico
UninstallDisplayIcon={app}\SyncToyNext.exe
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\SyncToyNext.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SyncToyNextCmd.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\SyncToyNext.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SyncToyNext.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SyncToyNext.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
