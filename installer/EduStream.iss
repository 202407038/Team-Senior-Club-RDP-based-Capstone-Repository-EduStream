#ifndef PackageDir
  #error PackageDir must point to self-contained Server and Client publish folders.
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0-preview.1"
#endif
#ifndef FileVersion
  #define FileVersion "0.1.0.0"
#endif

[Setup]
AppId=EduStream.Desktop
AppName=EduStream
AppVersion={#AppVersion}
AppPublisher=EduStream Capstone Team
AppPublisherURL=https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream
DefaultDirName={localappdata}\Programs\EduStream
DefaultGroupName=EduStream
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir={#InstallerOutputDir}
OutputBaseFilename=EduStream-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousSetupType=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=EduStream
InfoBeforeFile=INSTALL_NOTICE.txt
VersionInfoVersion={#FileVersion}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "student"; Description: "학생용 - 강의 참여"
Name: "professor"; Description: "교수자용 - 강의 개설 및 화면 공유"
Name: "both"; Description: "교수자용 + 학생용 - 시연 및 테스트"

[Components]
Name: "client"; Description: "EduStream 학생"; Types: student both
Name: "server"; Description: "EduStream 교수자"; Types: professor both

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 실행 아이콘 만들기"; GroupDescription: "바로가기:"

[Files]
Source: "{#PackageDir}\Server\*"; DestDir: "{app}\Server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\Client\*"; DestDir: "{app}\Client"; Components: client; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "INSTALL_NOTICE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\EduStream 교수자"; Filename: "{app}\Server\EduStream.Server.exe"; WorkingDir: "{app}\Server"; Components: server
Name: "{group}\EduStream 학생"; Filename: "{app}\Client\EduStream.Client.exe"; WorkingDir: "{app}\Client"; Components: client
Name: "{autodesktop}\EduStream 교수자"; Filename: "{app}\Server\EduStream.Server.exe"; WorkingDir: "{app}\Server"; Components: server; Tasks: desktopicon
Name: "{autodesktop}\EduStream 학생"; Filename: "{app}\Client\EduStream.Client.exe"; WorkingDir: "{app}\Client"; Components: client; Tasks: desktopicon

[Run]
Filename: "{app}\Server\EduStream.Server.exe"; Description: "EduStream 교수자 실행"; Components: server; Flags: nowait postinstall skipifsilent
Filename: "{app}\Client\EduStream.Client.exe"; Description: "EduStream 학생 실행"; Components: client; Flags: nowait postinstall skipifsilent
