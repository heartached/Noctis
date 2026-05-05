; Noctis — Inno Setup Installer Script
; Builds a setup.exe from the publish\win-x64\ output.
; Compile with: ISCC.exe installer.iss

#define MyAppName "Noctis"
#define MyAppVersion "1.1.2"
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\win-x64\Noctis.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\win-x64\Noctis.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\win-x64\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\win-x64\libvlc\*"; DestDir: "{app}\libvlc"; Flags: ignoreversion recursesubdirs createallsubdirs

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
