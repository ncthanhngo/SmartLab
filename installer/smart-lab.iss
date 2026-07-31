; Smart Lab installer.
;
; Installs per user, into %LOCALAPPDATA%\Programs\Smart Lab, and asks for no
; elevation to do it. That is a deliberate pair of choices rather than the default:
;
;   The app already elevates when it has to, one prompt at a time, for the operation
;   that needs it - an elevated worker for the Windows repair tools, a UAC prompt per
;   boot repair. Installing the whole thing to Program Files would add a prompt for
;   the parts that never needed one.
;
;   It also has to be able to replace itself. About downloads a newer release and
;   copies it over the installation; under Program Files that copy needs Administrator
;   the app does not have, and the updater would refuse rather than half-write a
;   working install.
;
; Built by tools/build-release.ps1, which passes AppVersion and SourceDir in.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\SmartLab-" + AppVersion + "-win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "Smart Lab"
#define AppPublisher "EVSE Lab"
#define AppUrl "https://github.com/ncthanhngo/SmartLab"
#define AppExe "SmartLab.App.exe"

[Setup]
; Never regenerate this. The AppId is what tells Windows an install is an upgrade of
; the same product rather than a second copy of it, and a new one would leave the old
; version installed alongside.
AppId={{8D4E7C21-5A96-4F3B-9E12-6C0B7A45D3F8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir={#OutputDir}
OutputBaseFilename=SmartLabSetup-{#AppVersion}
SetupIconFile=..\src\SmartLab.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

; The payload is a self-contained .NET build - about 180 MB of mostly compressible
; assemblies. LZMA at max takes the setup to roughly the size of the zip.
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
; Unticked. The app has its own Settings toggle for this, which registers under
; HKCU\...\Run and starts it in the tray; ticking it here as well would give two
; owners to one behaviour.
Name: "startup"; Description: "Start Smart Lab when I sign in (tray only)"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Written by the app beside itself, so an uninstall that left them would leave a
; folder behind that looks like a failed removal.
Type: files; Name: "{app}\binding-errors.txt"
Type: filesandordirs; Name: "{app}\logs"

[Code]
{ Refuses to install over a running copy. Files would be locked, the copy would half
  succeed, and what was left would be two versions mixed in one folder. }
function InitializeSetup(): Boolean;
var
  Running: Boolean;
begin
  Running := CheckForMutexes('SmartLab.App.Singleton');

  if Running then
  begin
    MsgBox('Smart Lab is running. Close it (including the tray icon) and run this ' +
           'installer again.', mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;
