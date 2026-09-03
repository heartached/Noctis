; Noctis — Inno Setup Installer Script
; Builds a setup.exe from the publish\win-x64\ output.
; Compile with: ISCC.exe installer.iss

#define MyAppName "Noctis"
#define MyAppVersion "1.4.7"
#define MyAppPublisher "heartached"
#define MyAppExeName "Noctis.exe"
#define MyAppURL "https://github.com/heartached/Noctis"

[Setup]
AppId={{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=installer-output
OutputBaseFilename=Noctis-v{#MyAppVersion}-Setup
SetupIconFile=src\Noctis\Assets\Icons\Noctis.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Tell Explorer associations changed so the Open-with list refreshes without a re-login.
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Add Noctis to &Open with / Default apps for audio files"; GroupDescription: "File types:"

[Registry]
; Per-user (PrivilegesRequired=lowest, so HKA = HKCU). Mirrors Helpers/WindowsFileAssociations.cs,
; which does the same for portable / winget installs from Settings -> General. Windows 10/11
; never let an installer take the default itself; these keys make Noctis a candidate.
Root: HKA; Subkey: "Software\Classes\Noctis.AudioFile"; ValueType: string; ValueName: ""; ValueData: "Audio File"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Noctis.AudioFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Noctis.AudioFile\shell\open"; ValueType: string; ValueName: ""; ValueData: "Play with Noctis"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Noctis.AudioFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Noctis"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Music player"; Tasks: fileassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Noctis"; ValueData: "Software\Noctis\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp3"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flac"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogg"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".oga"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.oga\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4a"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wav"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wma"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wma\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aac"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".opus"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aiff"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aiff\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aif"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aif\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aifc"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aifc\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ape"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ape\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wv"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wv\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".alac"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.alac\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".dsf"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.dsf\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Noctis\Capabilities\FileAssociations"; ValueType: string; ValueName: ".dff"; ValueData: "Noctis.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.dff\OpenWithProgids"; ValueType: none; ValueName: "Noctis.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc

[Files]
; Copy everything CI published rather than an allowlist of patterns. The old
; hand-written list (Noctis.exe + *.dll + libvlc\*) silently omitted ffmpeg.exe, which
; CI pins and SHA-256 verifies specifically so users don't need a system install — so
; every winget / Chocolatey / Setup.exe user lost the Audio Converter, the ReplayGain
; scanner and share-clip video export unless they happened to have ffmpeg on PATH.
; A wildcard cannot drift from what CI actually produces.
; *.pdb is excluded: it ships full source paths and internal symbol names to end users,
; and nothing in the app consumes it.
Source: "publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// On silent installs (the in-app updater path), Inno skips every [Run] entry
// flagged `postinstall skipifsilent`, so the app would never relaunch after
// "Install & Restart". Launch the new exe ourselves at ssDone in that case.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssDone) and WizardSilent() then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '',
         SW_SHOW, ewNoWait, ResultCode);
end;
