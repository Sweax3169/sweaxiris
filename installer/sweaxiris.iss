; SweaxIris installer (Inno Setup 6)
; ISCC /DAppVer=1.0.0 /DSrc=<publish folder> /O<out> installer\sweaxiris.iss
; Installs for the current user only (no admin), into %LOCALAPPDATA%\Programs\SweaxIris.

#ifndef AppVer
  #define AppVer "1.0.0"
#endif
#ifndef Src
  #define Src "..\out\app"
#endif

[Setup]
AppId={{9C4E2B71-6A3D-4F8E-B5C2-71D0A9E3F4B6}
AppName=SweaxIris
AppVersion={#AppVer}
AppVerName=SweaxIris {#AppVer}
AppPublisher=Sweax Studios
AppPublisherURL=https://github.com/Sweax3169/sweaxiris
AppSupportURL=https://github.com/Sweax3169/sweaxiris/issues
AppUpdatesURL=https://github.com/Sweax3169/sweaxiris/releases
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={localappdata}\Programs\SweaxIris
DisableDirPage=auto
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\SweaxIris.exe
UninstallDisplayName=SweaxIris {#AppVer}
LicenseFile=..\LICENSE
OutputBaseFilename=SweaxIris-{#AppVer}-Setup
SetupIconFile=..\src\SweaxIris\Assets\sweaxiris.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
AppMutex=SweaxIris.SingleInstance.81f3
CloseApplications=yes
SetupLogging=yes
MinVersion=10.0.19041

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.AutoStart=Start SweaxIris with Windows (the shortcuts always work)
tr.AutoStart=SweaxIris Windows ile başlasın (kısayollar hep çalışır)
en.Desktop=Create a desktop shortcut
tr.Desktop=Masaüstü kısayolu oluştur

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "desktop"; Description: "{cm:Desktop}"; Flags: unchecked

[Files]
Source: "{#Src}\SweaxIris.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\SweaxIris\Assets\Fonts\IBMPlex-OFL.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\SweaxIris"; Filename: "{app}\SweaxIris.exe"; Comment: "Screen recorder"
Name: "{userdesktop}\SweaxIris"; Filename: "{app}\SweaxIris.exe"; Tasks: desktop

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SweaxIris"; ValueData: """{app}\SweaxIris.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\SweaxIris.exe"; Description: "{cm:LaunchProgram,SweaxIris}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\SweaxIris.exe"; Parameters: "--quit"; Flags: runhidden skipifdoesntexist; RunOnceId: "QuitSweaxIris"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\ffmpeg"
