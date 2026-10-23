#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
[Setup]
AppId={{7D4B4EBD-1C31-4D62-A046-3C30E6FD92ED}
AppName=译栈 LocaleStack
AppVersion={#AppVersion}
AppPublisher=LocaleStack
AppPublisherURL=https://github.com/tkxs/LocaleStack
AppSupportURL=https://github.com/tkxs/LocaleStack/issues
DefaultDirName={localappdata}\Programs\LocaleStack
DefaultGroupName=译栈 LocaleStack
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=LocaleStack-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\LocaleStack.exe
SetupIconFile=..\assets\app.ico
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
AppMutex=LocaleStack.Desktop
VersionInfoVersion={#AppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{group}\译栈 LocaleStack"; Filename: "{app}\LocaleStack.exe"
Name: "{group}\译栈 维护者工作台"; Filename: "{app}\LocaleStack.exe"; Parameters: "--maintainer"
Name: "{autodesktop}\译栈 LocaleStack"; Filename: "{app}\LocaleStack.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LocaleStack.exe"; Description: "{cm:LaunchProgram,译栈 LocaleStack}"; Flags: nowait postinstall skipifsilent

; Uninstall removes program files only; game backups and LocalAppData preferences are retained.
