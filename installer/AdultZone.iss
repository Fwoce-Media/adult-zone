; Setup.exe for Adult Zone.
;
; Built automatically by the GitHub release workflow. To build it by hand,
; publish the app into ..\publish first, then open this file in Inno Setup 6
; and press Compile.
;
; Installs per user, so Windows never shows an administrator prompt.

#define AppName      "Adult Zone"
#define AppPublisher "Fwoce Media"
#define AppExe       "AdultZone.exe"
#ifndef AppVersion
  #define AppVersion "3.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
; 2.x's AppId, so this installs over 2.x in place.
AppId={{7C4E5A92-3D18-4B6E-9A2C-ADULTZONE0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://fwoce-media.github.io/
AppSupportURL=https://github.com/Fwoce-Media/adult-zone/issues
DefaultDirName={autopf}\AdultZone
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=AdultZone-{#AppVersion}-Setup
SetupIconFile=..\src\AdultZone.Desktop\adultzone.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; What 2.x left in the program folder: its web interface.
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\assets"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what setup put here. The library lives in the user's profile and stays.
Type: filesandordirs; Name: "{app}\libvlc"
