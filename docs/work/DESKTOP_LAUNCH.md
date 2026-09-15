# 더블클릭 실행

## 평소 실행

바탕화면의 **EduStream 교수자**, **EduStream 학생** 바로가기를 더블클릭합니다. 터미널과 매번 빌드는 필요 없습니다.

- 교수자: 세션 열기 → WDS 공유 시작.
- 학생: 설정 → 이름/호스트/포트 → 세션 참여 → 초대 비밀번호 입력 → RDP 연결.
- 실제 사용 순서는 [실행 가이드](./RUN_GUIDE.md)를 따릅니다. 바로가기는 앱을 열 뿐 세션/화면 공유를 자동 시작하지 않습니다.

## 최초 준비 또는 코드 변경 후 갱신

소스 작업본을 실행본으로 만드는 PC에는 .NET SDK가 필요합니다.

1. 원하는 최신 코드의 저장소 폴더를 엽니다.
2. 저장소 루트의 **Setup-EduStream.cmd**를 더블클릭합니다.
3. 두 앱 게시가 끝나고 Ready 안내가 나오면 아무 키나 눌러 준비 창을 닫습니다.
4. 이후에는 바탕화면 바로가기를 사용합니다.

최초 준비/갱신 때만 명령 창과 빌드가 실행됩니다. 이미 실행 중인 앱은 자동 종료하지 않습니다. 준비 후 이전 앱 창을 닫고 바로가기로 다시 열면 새 버전이 실행됩니다.

게시 폴더는 `%LOCALAPPDATA%\EduStream\<생성 시각-식별자>\`입니다. 소스 폴더와 분리되므로 저장소를 이동해도 기존 바로가기는 유지됩니다.

- Server 폴더: EduStream.Server.exe.
- Client 폴더: EduStream.Client.exe.
- 두 폴더에는 .NET 실행 환경이 포함됩니다. 이 게시본의 실행 PC에 빌드용 SDK나 별도 .NET 런타임을 설치할 필요는 없습니다.
- Windows WDS/COM 지원은 별도 OS 조건입니다. 실행 환경 포함이 WDS 자체 설치/지원 보장을 뜻하지 않습니다.
- 다른 Windows x64 PC로 옮길 때는 필요한 **Server 또는 Client 폴더 전체**를 복사합니다. exe만 복사하지 않습니다. 복사한 폴더 안의 exe를 더블클릭하면 됩니다.
- 다른 PC에 기존 .lnk만 복사하면 원래 PC 경로를 참조하므로 작동하지 않습니다. 옮긴 exe의 바로가기를 새로 만듭니다.

## 기존 파일 보존

- 새 버전은 새 폴더에 생성하며 기존 실행본을 지우거나 덮어쓰지 않습니다.
- 두 앱 게시와 필수 파일 검사가 성공한 뒤에만 이 스크립트가 만든 바로가기를 갱신합니다.
- 같은 이름의 사용자 바로가기가 있으면 덮어쓰지 않고 중단합니다. 사용자 바로가기의 이름을 바꾼 뒤 다시 준비합니다.
- 게시 중 실패하면 생성 중이던 폴더는 진단용으로 남습니다. 기존 바로가기를 계속 사용할 수 있습니다.
- 실행본은 자동으로 Git 최신 코드를 반영하지 않습니다. 코드 변경 후 준비 파일을 다시 실행해야 합니다.
- 게시 파일·바로가기는 로컬 산출물이며 Git 커밋에 포함하지 않습니다.

## 개발자 명령과 검사

```powershell
./scripts/Publish-EduStreamDesktop.ps1 -CreateDesktopShortcuts
```

바로가기 없이 다른 위치에 게시하려면 **아직 없는 새 폴더**를 지정합니다.

```powershell
./scripts/Publish-EduStreamDesktop.ps1 -OutputDirectory 'C:\EduStreamPackage-New'
```

출력 경로를 사용해 패키지/바로가기와 GUI 시작을 검사합니다.

```powershell
./scripts/Test-EduStreamDesktop.ps1 -PackageDirectory '실제 게시 폴더' -ShortcutDirectory ([Environment]::GetFolderPath('Desktop')) -LaunchSmokeTest
```

GUI 시작 검사는 테스트가 만든 앱을 숨김으로 실행해 메시지 루프 시작을 확인하고 해당 프로세스만 종료합니다. 실제 화면 공유나 네트워크 연결 검증을 대체하지 않습니다.
