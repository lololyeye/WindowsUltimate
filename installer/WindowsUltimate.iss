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
AppPublisher=zero
AppPublisherURL=https://lololyeye.github.io/WindowsUltimate/
AppSupportURL=https://github.com/lololyeye/WindowsUltimate/issues
AppCopyright=Copyright (C) 2026 zero
; Licence page of the wizard: LICENSE.md (English and French), copied to license.txt with a UTF-8 BOM
; by the release workflow so Inno Setup reads its accents correctly.
#if FileExists(AddBackslash(SourcePath) + "license.txt")
LicenseFile=license.txt
#endif
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
VersionInfoCompany=zero
VersionInfoCopyright=Copyright (C) 2026 zero

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The licence and the notices required by the bundled .NET runtime, WPF and Inno Setup.
Source: "..\LICENSE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Launch unelevated so the app starts as a standard user, as designed. Not skipped in silent mode,
; so the app reopens by itself after an in-app update.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall runasoriginaluser

; Uninstall deliberately keeps %LOCALAPPDATA%\WindowsUltimate: it holds the change journal needed to
; undo optimizations. Users are told to run "Undo all" before uninstalling (see README).

[CustomMessages]
english.KeepDataNote=Windows Ultimate will be removed, but its folder AppData\Local\WindowsUltimate (change journal, logs, settings) is kept, and the Windows settings it changed stay as they are.%n%nTo restore them first, choose No, open Windows Ultimate and use History & undo, then Undo all.%n%nUninstall now?
french.KeepDataNote=Windows Ultimate va être supprimé, mais son dossier AppData\Local\WindowsUltimate (journal des modifications, journaux, paramètres) est conservé, et les réglages de Windows qu'il a modifiés restent tels quels.%n%nPour les rétablir d'abord, choisissez Non, ouvrez Windows Ultimate et utilisez Historique et annulation, puis Tout annuler.%n%nDésinstaller maintenant ?

[Code]
// Says what uninstalling leaves behind before anything is removed. Skipped in silent mode.
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not UninstallSilent then
    Result := MsgBox(CustomMessage('KeepDataNote'), mbConfirmation, MB_YESNO) = IDYES;
end;
