; CaptureFix installer (Inno Setup 6).
; Build: iscc /DAppVersion=1.0.0 /DPublishDir=..\publish installer\CaptureFix.iss
; Per-user install (no admin prompt), so CaptureFix can update itself silently.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; Never change AppId: it's how updates find the existing install.
AppId={{5E0C3B6E-2F7A-4C59-9B1D-8F6A2D4C7E31}
AppName=CaptureFix
AppVersion={#AppVersion}
AppVerName=CaptureFix {#AppVersion}
AppPublisher=NamiSwan222
AppPublisherURL=https://github.com/NamiSwan222/capturefix
AppSupportURL=https://github.com/NamiSwan222/capturefix/issues
AppUpdatesURL=https://github.com/NamiSwan222/capturefix/releases
DefaultDirName={localappdata}\Programs\CaptureFix
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=CaptureFix-Setup-{#AppVersion}
SetupIconFile=..\src\CaptureFix\Assets\CaptureFix.ico
UninstallDisplayIcon={app}\CaptureFix.exe
UninstallDisplayName=CaptureFix
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CaptureFix"; Filename: "{app}\CaptureFix.exe"
Name: "{autodesktop}\CaptureFix"; Filename: "{app}\CaptureFix.exe"; Tasks: desktopicon

[Run]
; Normal install: offer to open it. Update (/UPDATE): start the new version, unless the update
; ran because CaptureFix was closed (/NOLAUNCH).
Filename: "{app}\CaptureFix.exe"; Description: "Open CaptureFix now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\CaptureFix.exe"; Parameters: "--updated"; Flags: nowait; Check: IsUpdate and not NoLaunch

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM CaptureFix.exe /F"; Flags: runhidden; RunOnceId: "StopCaptureFix"

[Code]
function HasParam(Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

function IsUpdate: Boolean;
begin
  Result := HasParam('/UPDATE');
end;

function NoLaunch: Boolean;
begin
  Result := HasParam('/NOLAUNCH');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Updates go straight through without questions. }
  Result := IsUpdate;
end;
