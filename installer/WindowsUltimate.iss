; Inno Setup script for Windows Ultimate.
; Build the app first:
;   dotnet publish src\WindowsUltimate.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish\win-x64
; then compile this script with Inno Setup 6 (ISCC.exe installer\WindowsUltimate.iss).

#define AppName "Windows Ultimate"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "WindowsUltimate.exe"
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
AppId={{6E3C2B7A-4F1D-4C55-9B8E-2A7D1F0C9E41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=WindowsUltimate-{#AppVersion}-setup
SetupIconFile=..\src\WindowsUltimate.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 only (build 22000+).
MinVersion=10.0.22000
; Installing to Program Files needs elevation; the app itself runs as a standard user (asInvoker manifest).
PrivilegesRequired=admin
; In-app updates run this installer with /SILENT /CLOSEAPPLICATIONS: close the running app, keep the install folder.
CloseApplications=force
RestartApplications=no
UsePreviousAppDir=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Launch unelevated so the app starts as a standard user, as designed. Not skipped in silent mode,
; so the app reopens by itself after an in-app update.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall runasoriginaluser

; Uninstall deliberately keeps %LOCALAPPDATA%\WindowsUltimate: it holds the change journal needed to
; undo optimizations. Users are told to run "Undo all" before uninstalling (see README).
