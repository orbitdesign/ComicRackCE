; Define version and setup filename with iscc.exe /DMyAppVersion=v1.0 /DMyAppSetupFile=ComicRackSetup_v1.0 Installer.iss
#define MyAppName "ComicRack CEO"
; The folder it installs to (under Program Files); the name shown in Windows Apps and the shortcuts is MyAppName.
#define MyAppDir "ComicRack CEO"
#ifndef MyAppVersion
#define MyAppVersion "v0.9.186"
#endif
#ifndef MyAppSetupFile
#define MyAppSetupFile "ComicRackSetup"
#endif
#define MyAppPublisher "orbitdesign"
#define MyAppURL "https://github.com/maforget/ComicRackCE"
#define MyAppExeName "ComicRack.exe"

; The folder holding VC_redist.x64.exe (the Microsoft Visual C++ 2022 runtime, from https://aka.ms/vs/17/release/vc_redist.x64.exe).
#ifndef RedistDir
#define RedistDir "ComicRack\bin\Release\net48\_CommonRedist"
#endif

; The translator helper's files (the .py files, run.bat, setup.bat ...): give their folder with /DHelperSrc=... to have the
; "Translator helper" component in the setup. Without it the setup is the application only.
; Example: /DHelperSrc="S:\FROG\Claude AI\Comicrack\ComicTranslator-src\ComicTranslator"

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
; (To generate a new GUID, click Tools | Generate GUID inside the IDE.)
; A separate identity from stock ComicRack Community Edition's own installer, so Windows
; never treats this custom build as an upgrade of - or gets confused alongside - a stock
; install of ComicRack CE on the same machine. Generated fresh, not reused from anywhere.
AppId={{8ADBD2BA-0B19-4C72-9D0B-E11EC11F733E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
;AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppDir}
ChangesAssociations=yes
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=ComicRack\bin\Release\net48\License.txt
PrivilegesRequired=admin
; /CURRENTUSER on the command line makes a per-user install (used to try the setup out without touching the machine)
PrivilegesRequiredOverridesAllowed=commandline
UsedUserAreasWarning=no
OutputDir=.
OutputBaseFilename={#MyAppSetupFile}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
AlwaysShowComponentsList=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=ComicRack\Icons\uninst_103.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
RestartIfNeededByRun=false

[Messages]
// define wizard title and tray status msg
// both are normally defined in innosetup's default.isl (install folder)
SetupAppTitle = {#MyAppName} {#MyAppVersion} Setup
SetupWindowTitle = {#MyAppName} {#MyAppVersion} Setup 

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; Options displayed during setup (component selection)
[Types]
Name: "full";    Description: "Full installation";
Name: "typical"; Description: "Typical installation";
Name: "compact"; Description: "Compact installation";
Name: "custom";  Description: "Custom installation"; Flags: iscustom

; The compotent definition
[Components]
Name: "app";       Description: "{#MyAppName} (Required)";                    Types: full typical compact custom; Flags: fixed
Name: "start_menu";Description: "Start Menu";                               Types: full typical
Name: "desktop";   Description: "Desktop Shortcut";                         Types: full typical
Name: "associate"; Description: "Associate eComic extensions";              Types: full typical
Name: "languages"; Description: "Language Packs";                           Types: full
Name: "additional";Description: "Additional images, icons and backgrounds"; Types: full
#ifdef HelperSrc
Name: "translator";Description: "Translator helper (reads and translates page text; setting it up needs Python 3.10+ and Ollama)"; Types: full
#endif

[Files]
; NOTE: Don't use "Flags: ignoreversion" on any shared system files
Source: "ComicRack\bin\Release\net48\*.dll"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\Changes.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\{#MyAppExeName}.config"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\ComicRack.ini"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\DefaultLists.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\License.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\NewsTemplate.html"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\ReadMe.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme; Components: app
Source: "ComicRack\bin\Release\net48\Help\*"; DestDir: "{app}\Help"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\Languages\*"; DestDir: "{app}\Languages"; Flags: ignoreversion; Components: languages
Source: "ComicRack\bin\Release\net48\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\Resources\x86\*"; DestDir: "{app}\Resources\x86"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\Resources\x64\*"; DestDir: "{app}\Resources\x64"; Flags: ignoreversion; Components: app
Source: "ComicRack\bin\Release\net48\Resources\Icons\*"; DestDir: "{app}\Resources\Icons"; Flags: ignoreversion; Components: additional
Source: "ComicRack\bin\Release\net48\Resources\Textures\*"; DestDir: "{app}\Resources\Textures"; Flags: ignoreversion recursesubdirs; Components: additional
Source: "ComicRack\bin\Release\net48\Scripts\*"; DestDir: "{app}\Scripts"; Flags: ignoreversion; Components: app
Source: "{#RedistDir}\VC_redist.x64.exe"; DestDir: {tmp}; Flags: dontcopy
#ifdef HelperSrc
; The helper lives where the user may write: it keeps its settings, fonts, models and Python environment beside its files.
Source: "{#HelperSrc}\*.py"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
Source: "{#HelperSrc}\run.bat"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
Source: "{#HelperSrc}\setup.bat"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
Source: "{#HelperSrc}\requirements.txt"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
Source: "{#HelperSrc}\README.md"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
Source: "{#HelperSrc}\comictranslator.ico"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
Source: "{#HelperSrc}\logo.png"; DestDir: "{localappdata}\ComicTranslator"; Flags: ignoreversion; Components: translator
#endif

[Registry]
; Comics
Root: HKA; Subkey: "Software\Classes\cYo.ComicRack";                       ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "eComic"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRack\DefaultIcon";           ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"",1"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRack\shell";                 ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "open"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRack\shell\open";            ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "Open eComic with ComicRack CE"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRack\shell\open\command";    ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}""  ""%1"""

; Comic Lists
Root: HKA; Subkey: "Software\Classes\cYo.ComicList";                    ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "eComic List"
Root: HKA; Subkey: "Software\Classes\cYo.ComicList\DefaultIcon";        ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"",2"
Root: HKA; Subkey: "Software\Classes\cYo.ComicList\shell";              ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "open"
Root: HKA; Subkey: "Software\Classes\cYo.ComicList\shell\open";         ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "Import eComic List into ComicRack CE"
Root: HKA; Subkey: "Software\Classes\cYo.ComicList\shell\open\command"; ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"" -il ""%1"""

; ComicRack Plugins
Root: HKA; Subkey: "Software\Classes\cYo.ComicRackPlugin";                       ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "ComicRack Plugin"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRackPlugin\DefaultIcon";           ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"",3"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRackPlugin\shell";                 ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "open"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRackPlugin\shell\open";            ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "Install Plugin into ComicRack CE"
Root: HKA; Subkey: "Software\Classes\cYo.ComicRackPlugin\shell\open\command";    ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"" -ip ""%1"""

; Extensions
Root: HKA; Subkey: "Software\Classes\.cbz";                                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbz\OpenWithProgIDs";                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbr";                                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbr\OpenWithProgIDs";                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cb7";                                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cb7\OpenWithProgIDs";                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbt";                                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbt\OpenWithProgIDs";                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbw";                                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbw\OpenWithProgIDs";                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicRack"
Root: HKA; Subkey: "Software\Classes\.cbl";                                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicList"
Root: HKA; Subkey: "Software\Classes\.cbl\OpenWithProgIDs";                   ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicList"
Root: HKA; Subkey: "Software\Classes\.crplugin";                              ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueData: "cYo.ComicRackPlugin"
Root: HKA; Subkey: "Software\Classes\.crplugin\OpenWithProgIDs";              ValueType: string; Flags: uninsdeletevalue; Components: associate; ValueName: "cYo.ComicRackPlugin"

; Application specific
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; Flags: uninsdeletevalue; ValueData: "{autopf}\{#MyAppDir}\{#MyAppExeName}"
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}";                       ValueType: string; Flags: uninsdeletevalue; ValueData: "{#MyAppName}"; ValueName: "FriendlyAppName"
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\DefaultIcon";           ValueType: string; Flags: uninsdeletevalue; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"",1"
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open";            ValueType: string; Flags: uninsdeletevalue; ValueData: "{#MyAppName}"; ValueName: "FriendlyAppName"
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command";    ValueType: string; Flags: uninsdeletevalue; ValueData: """{autopf}\{#MyAppDir}\{#MyAppExeName}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes";        ValueType: string; Flags: uninsdeletevalue; ValueName: ".cb7"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes";        ValueType: string; Flags: uninsdeletevalue; ValueName: ".cbz"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes";        ValueType: string; Flags: uninsdeletevalue; ValueName: ".cbr"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes";        ValueType: string; Flags: uninsdeletevalue; ValueName: ".cbt"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes";        ValueType: string; Flags: uninsdeletevalue; ValueName: ".cbw"; ValueData: ""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}";                Components: start_menu
Name: "{group}\{cm:ProgramOnTheWeb,{#MyAppName}}"; Filename: "{#MyAppURL}";     Components: start_menu
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Components: start_menu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}";          Components: desktop
#ifdef HelperSrc
Name: "{group}\Translator helper"; Filename: "{localappdata}\ComicTranslator\run.bat"; WorkingDir: "{localappdata}\ComicTranslator"; IconFilename: "{localappdata}\ComicTranslator\comictranslator.ico"; Components: translator and start_menu
#endif

[InstallDelete]
// Remove old dll's from old version before their location was changed
Type: files; Name: "{app}\Resources\libwebp32.dll"
Type: files; Name: "{app}\Resources\libwebp64.dll"
Type: files; Name: "{app}\Resources\aom.dll"
Type: files; Name: "{app}\Resources\libde265.dll"
Type: files; Name: "{app}\Resources\libheif.dll"
Type: files; Name: "{app}\Resources\libx265.dll"
Type: files; Name: "{app}\Resources\7z.dll"
Type: files; Name: "{app}\Resources\7z.exe"
Type: files; Name: "{app}\Resources\7z64.dll"
Type: files; Name: "{app}\Resources\7za64.exe"
Type: files; Name: "{app}\Resources\jxl.dll"
Type: files; Name: "{app}\Resources\jxl_cms.dll"
Type: files; Name: "{app}\Resources\brotlienc.dll"
Type: files; Name: "{app}\Resources\brotlidec.dll"
Type: files; Name: "{app}\Resources\brotlicommon.dll"
Type: files; Name: "{app}\Resources\pdfium_x86.dll"
Type: files; Name: "{app}\Resources\pdfium_x64.dll"

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /passive /norestart"; \
    Check: Is64BitInstallMode and VC2022RedistNeedsInstall; \
    Flags: waituntilterminated; \
    StatusMsg: "Installing VC++ 2022 redistributables..."
#ifdef HelperSrc
Filename: "{cmd}"; Parameters: "/c ""{localappdata}\ComicTranslator\setup.bat"""; WorkingDir: "{localappdata}\ComicTranslator"; Description: "Set up the translator helper now (needs Python 3.10+ and internet; takes several minutes)"; Flags: postinstall unchecked skipifsilent; Components: translator
#endif
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Set the minimum .NET Framework version release code. 528040 = .NET Framework 4.8
const
  NETFrameworkLabel = '.NET Framework 4.8';
const
  NETFrameworkMinimum = 528040;
const
  NETFrameworkDownload = 'https://go.microsoft.com/fwlink/?linkid=2088631';
const
  NETFrameworkFilename = 'ndp48-x86-x64-allos-enu.exe';

var
  NETFrameworkVersion: Cardinal;
var
  DownloadPage: TDownloadWizardPage;
var
  ResultCode: Integer;

// Log download progress to log file
function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log(Format('Successfully downloaded file to {tmp}: %s', [FileName]));
  Result := True;
end;

// The standard ComicRack Community Edition (the entry of its own installer): this build replaces it, so the setup offers to remove it first.
// Only its program files go: the library, settings and translation data live in the user's profile and are kept.
const
  StockUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{0FA63C63-846C-49B7-9A4B-553EF8EBEF0B}_is1';

function StockUninstallCommand(var Command: String): Boolean;
begin
  Result := RegQueryStringValue(HKLM64, StockUninstallKey, 'UninstallString', Command)
         or RegQueryStringValue(HKLM32, StockUninstallKey, 'UninstallString', Command);
end;

function InitializeSetup(): Boolean;
var
  Command: String;
  Code: Integer;
begin
  Result := True;
  if StockUninstallCommand(Command) then
  begin
    if SuppressibleMsgBox('The standard ComicRack Community Edition is installed on this PC.' + #13#10#13#10 +
         'This build replaces it. Remove the standard one now?' + #13#10 +
         '(Your library, settings and reading progress are kept.)', mbConfirmation, MB_YESNO, IDNO) = IDYES then
    begin
      Command := RemoveQuotes(Command);
      if Exec(Command, '/SILENT /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, Code) then
        Log(Format('The standard ComicRack was removed (exit code %d).', [Code]))
      else
        Log(Format('The standard ComicRack could not be removed (error %d).', [Code]));
    end;
  end;
end;

// When the wizard form loads
procedure InitializeWizard;
begin
  // Create the download page
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

// Download and run the .NET Framework setup
function DownloadNETFramework(): Boolean;
begin
  DownloadPage.Clear;
  DownloadPage.Add(NETFrameworkDownload, NETFrameworkFilename, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download; // This downloads the file to {tmp}
    except
      if DownloadPage.AbortedByUser then
        Log('Aborted by user.')
      else
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
        Log(AddPeriod(GetExceptionMessage))
      Result := False;
    end;
    if Exec(ExpandConstant('{tmp}\'+NETFrameworkFilename), '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then begin
      Result := True;
    end
    else begin
      Log(Format('%s installation failed: [Result Code: %d] {tmp}\%s', [NETFrameworkLabel, ResultCode, NETFrameworkFilename]));
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function VC2022RedistNeedsInstall: Boolean;
var 
  Version: String;
begin
  if RegQueryStringValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version',
       Version) then
  begin
    // Is the installed version at least 14.44 ? 
    Log('VC Redist Version check : found ' + Version);
    Result := (CompareStr(Version, 'v14.44.35207.00')<0);
  end
  else 
  begin
    // Not even an old version installed
    Result := True;
  end;
  if (Result) then
  begin
    ExtractTemporaryFile('VC_redist.x64.exe');
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpReady then begin
    if RegQueryDWordValue(HKEY_LOCAL_MACHINE, 'Software\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', NETFrameworkVersion) then begin
      if (NETFrameworkVersion < NETFrameworkMinimum) then begin
        Log(Format('.NET Framework version (%s) is insufficient. Downloading %s.', [IntToStr(NETFrameworkVersion), NETFrameworkLabel]));
        Result := DownloadNETFramework()
      end else begin
        Log(Format('.NET Framework version (%s) is sufficient.', [IntToStr(NETFrameworkVersion)]));
        Result := True;
      end;
    end else begin
      Log(Format('.NET Framework is not installed. Downloading.', [IntToStr(NETFrameworkVersion)]));
      Result := DownloadNETFramework()
    end;
  end else
    Result := True;
end;
