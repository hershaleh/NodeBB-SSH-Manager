#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppExeName "NodeBB.SSHManager.exe"

[Setup]
AppId={{A2E78F6D-6F14-4A9D-8C25-6F7D41B2E8A1}
AppName=NodeBB SSH Manager
AppVersion={#AppVersion}
AppPublisher=hershaleh
AppPublisherURL=https://github.com/hershaleh/NodeBB-SSH-Manager
AppSupportURL=https://github.com/hershaleh/NodeBB-SSH-Manager/issues
DefaultDirName={localappdata}\Programs\NodeBB SSH Manager
DefaultGroupName=NodeBB SSH Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\artifacts
OutputBaseFilename=NodeBB-SSH-Manager-Setup-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no
Uninstallable=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NodeBB SSH Manager"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\NodeBB SSH Manager"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch NodeBB SSH Manager"; Flags: nowait postinstall skipifsilent
