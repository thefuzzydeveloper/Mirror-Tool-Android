; --- Configuration Variables ---
#define MyAppName "WiFiAutoStreamSync"
#define MyAppVersion "2.1"
#define MyAppPublisher "The Developer"
#define MyAppExeName "WiFiAutoStreamSync.exe"

[Setup]
; --- Application Information ---
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}

; --- Modern UI & Safeguards ---
WizardStyle=modern
SetupMutex={#MyAppName}SetupMutex
CloseApplications=yes
ChangesAssociations=yes

; --- License Agreement ---
LicenseFile=license.txt

; --- Installation Directory Settings (Per-User) ---
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest

; --- Modern Installer Tweaks ---
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

; --- Output Settings ---
OutputDir=.\
OutputBaseFilename=WiFiAutoStreamSync_Setup
SetupIconFile=icon.ico

; --- Compression Settings ---
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Source directory from build output
Source: "dist\Mirror tool\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean runtime logs and cached configurations on uninstall
Type: files; Name: "{app}\startup_debug.log"
Type: files; Name: "{%USERPROFILE}\.wifiautostreamsync_config.json"