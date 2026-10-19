; Capturix installer (Inno Setup 6). Build with installer\build-installer.ps1, which publishes the
; self-contained app first and passes the version in /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Capturix"
#define AppPublisher "ProtoHub"
#define AppUrl "https://protohub.digital/capturix/"
#define PublishDir "..\artifacts\publish\Capturix"

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{8C2F4A71-3D5E-4B9A-A6C1-7E0F2D9B5C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://protohub.digital/
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador de {#AppName}
; Per-user install into %LOCALAPPDATA%\Programs: no administrator rights needed.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; The capturer's single-instance mutex: setup asks to close Capturix before replacing files.
AppMutex=Capturix.Capture.{{6B1E3C52-7F0A-4C1D-9B8E-2A4F5D6C7E81}
CloseApplications=yes
SetupIconFile=..\assets\logo\capturix.ico
UninstallDisplayIcon={app}\Capturix.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=CapturixSetup-{#AppVersion}

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked
Name: "autostart"; Description: "Iniciar Capturix con Windows (en la bandeja del sistema)"; GroupDescription: "Inicio:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Capturix"; Filename: "{app}\Capturix.exe"; Comment: "Capturas, grabación y OCR desde la bandeja"
Name: "{group}\Capturix Editor"; Filename: "{app}\Capturix.Editor.exe"; Comment: "Editor y biblioteca de capturas"
Name: "{group}\Desinstalar Capturix"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Capturix"; Filename: "{app}\Capturix.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Capturix"; \
    ValueData: """{app}\Capturix.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Capturix.exe"; Description: "Iniciar Capturix ahora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the tray app so its files can be removed (captures and settings are kept).
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Capturix.exe /F"; Flags: runhidden; RunOnceId: "StopCapturix"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Capturix.Editor.exe /F"; Flags: runhidden; RunOnceId: "StopCapturixEditor"
