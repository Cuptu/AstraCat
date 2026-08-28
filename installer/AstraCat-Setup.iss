; AstraCat Inno Setup Script
#define MyAppName "AstraCat"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-DEV"
#endif
#define MyAppPublisher "Cuptu"
#define MyAppExeName "AstraCat.exe"
#ifndef BuildDistDir
  #define BuildDistDir "..\dist"
#endif
#ifndef MyAppOutputBaseFilename
  #define MyAppOutputBaseFilename "AstraCat-v0.1.0-DEV-Setup"
#endif

[Setup]
AppId={{D98C1242-7F32-47C0-B955-8D6E955FA01E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Cuptu/AstraCat
AppSupportURL=https://github.com/Cuptu/AstraCat/security/advisories/new
AppUpdatesURL=https://github.com/Cuptu/AstraCat/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#BuildDistDir}
OutputBaseFilename={#MyAppOutputBaseFilename}
SetupIconFile=..\Assets\Brand\AstraCat.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
VersionInfoCompany=Cuptu
VersionInfoDescription=AstraCat subtitle production application

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BuildDistDir}\AstraCat-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
