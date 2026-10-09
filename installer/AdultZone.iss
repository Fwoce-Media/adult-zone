; Setup.exe for Adult Zone.
;
; Built automatically by the GitHub release workflow. To build it by hand,
; publish the app into ..\publish first, then open this file in Inno Setup 6
; and press Compile.
;
; Asks for administrator rights when it starts, so it can install anywhere;
; Program Files is offered first.

#define AppName      "Adult Zone"
#define AppPublisher "Fwoce Media"
#define AppExe       "AdultZone.exe"
#define AppIdKey     "{7C4E5A92-3D18-4B6E-9A2C-ADULTZONE0001}_is1"
#ifndef AppVersion
  #define AppVersion "3.6.0"
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
DisableDirPage=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
OutputBaseFilename=AdultZone-{#AppVersion}-Setup
SetupIconFile=..\src\AdultZone.Desktop\adultzone.ico
WizardImageFile=wizard-100.bmp,wizard-125.bmp,wizard-150.bmp,wizard-175.bmp,wizard-200.bmp,wizard-225.bmp,wizard-250.bmp
WizardSmallImageFile=small-100.bmp,small-125.bmp,small-150.bmp,small-175.bmp,small-200.bmp,small-225.bmp,small-250.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "main"; Description: "{#AppName}"; Types: custom; Flags: fixed
Name: "ffmpeg"; Description: "FFmpeg (makes the thumbnails, hover previews and seek-bar frames, about 200 MB download)"; Types: custom

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
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; Only what setup put here. The library lives in the user's folders and stays.
Type: filesandordirs; Name: "{app}\libvlc"
Type: filesandordirs; Name: "{app}\ffmpeg"

[Code]
const
  FfmpegUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip';

var
  DownloadPage: TDownloadWizardPage;
  FfmpegPicked: Boolean;
  FfmpegFetched: Boolean;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

{ ffmpeg already beside the program, or anywhere on PATH. }
function FfmpegPresent: Boolean;
begin
  Result := FileExists(AddBackslash(WizardDirValue) + 'ffmpeg\ffmpeg.exe')
    or (FileSearch('ffmpeg.exe', GetEnv('PATH')) <> '');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading FFmpeg', 'Setup is fetching FFmpeg for {#AppName}.', @OnDownloadProgress);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Ticked when the computer has no ffmpeg, left unticked when it has one. }
  if (CurPageID = wpSelectComponents) and not FfmpegPicked then
  begin
    FfmpegPicked := True;
    if FfmpegPresent then WizardSelectComponents('!ffmpeg') else WizardSelectComponents('ffmpeg');
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsComponentSelected('ffmpeg') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(FfmpegUrl, 'ffmpeg.zip', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        FfmpegFetched := True;
      except
        if not DownloadPage.AbortedByUser then
          SuppressibleMsgBox('FFmpeg could not be downloaded: ' + GetExceptionMessage + #13#10#13#10 +
            '{#AppName} will still be installed. FFmpeg can be added later from its Settings.', mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

{ ffmpeg.exe and ffprobe.exe out of the archive, into an ffmpeg folder beside the program. }
procedure UnpackFfmpeg;
var
  Script, Zip, Dest: String;
  Code: Integer;
begin
  Zip := ExpandConstant('{tmp}\ffmpeg.zip');
  Dest := ExpandConstant('{app}\ffmpeg');
  WizardForm.StatusLabel.Caption := 'Installing FFmpeg...';
  Script :=
    '$ErrorActionPreference = ''Stop''; ' +
    '$t = Join-Path $env:TEMP (''ffmpeg-'' + [guid]::NewGuid()); ' +
    'New-Item -ItemType Directory -Force -Path $t | Out-Null; ' +
    'try { tar -xf ''' + Zip + ''' -C $t } catch { }; ' +
    'if (-not (Get-ChildItem -Path $t -Recurse -Filter ffmpeg.exe)) { Expand-Archive -LiteralPath ''' + Zip + ''' -DestinationPath $t -Force }; ' +
    'New-Item -ItemType Directory -Force -Path ''' + Dest + ''' | Out-Null; ' +
    'Get-ChildItem -Path $t -Recurse -Include ffmpeg.exe,ffprobe.exe | Copy-Item -Destination ''' + Dest + ''' -Force; ' +
    'Remove-Item -Recurse -Force $t';
  if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "' + Script + '"', '', SW_HIDE, ewWaitUntilTerminated, Code)
     or (Code <> 0) or not FileExists(Dest + '\ffmpeg.exe') then
    SuppressibleMsgBox('FFmpeg could not be unpacked. It can be added later from {#AppName}''s Settings.', mbInformation, MB_OK, IDOK);
end;

{ Earlier versions installed for one user only. That copy is removed first so
  there is one {#AppName} on the computer; the library is kept, as it always is. }
procedure RemovePerUserCopy;
var
  Uninstaller: String;
  Code: Integer;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdKey}', 'UninstallString', Uninstaller) then
  begin
    WizardForm.StatusLabel.Caption := 'Removing the earlier copy...';
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RemovePerUserCopy;
  if (CurStep = ssPostInstall) and FfmpegFetched then UnpackFfmpeg;
end;
