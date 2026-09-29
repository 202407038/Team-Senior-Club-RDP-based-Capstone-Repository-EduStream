# EduStream 설치와 실행

## ZIP을 풀고 바로 실행하기 (설치 불필요)

1. GitHub 메인 README의 **[실행용 ZIP 다운로드](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v0.1.0-preview.1/EduStream-Portable-win-x64.zip)**를 누릅니다.
2. 받은 ZIP을 우클릭 → **모두 압축 풀기 → 압축 풀기**.
3. 압축을 푼 `EduStream` 폴더에서 **EduStream 교수자.exe / EduStream 학생.exe** 중 사용할 역할을 더블클릭합니다.

설치, 관리자 권한, PowerShell 명령, 별도 .NET 설치가 필요하지 않습니다. ZIP 안에서 직접 실행하면 안 됩니다. 함께 들어 있는 DLL과 하위 폴더도 필요하므로 삭제하거나 EXE만 따로 이동하지 않습니다. 앱 폴더를 다른 위치로 옮길 때는 전체를 옮깁니다. 바탕화면에는 EXE의 바로가기를 만들어 사용할 수 있습니다.

업데이트는 실행 앱을 종료한 뒤 새 ZIP을 **새 폴더**에 풉니다. 수신한 강의 자료를 먼저 보관하고 이전 실행 폴더를 정리합니다. 자동 업데이트·자동 바로가기·설치 등록은 하지 않습니다.

## 설치형을 원하는 사람

**[Windows x64 설치 파일 다운로드](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v0.1.0-preview.1/EduStream-Setup.exe)**

1. GitHub 메인 README 맨 위의 설치 파일 다운로드를 누릅니다.
2. 받은 `EduStream-Setup.exe`를 더블클릭합니다.
3. 안내 확인 → **학생용 / 교수자용 / 교수자용 + 학생용** 중 사용할 역할 선택.
4. **바탕화면에 실행 아이콘 만들기**를 선택한 채 설치 → 완료.
5. 바탕화면 또는 시작 메뉴에서 **EduStream 학생 / EduStream 교수자**를 더블클릭합니다.

PowerShell, Git 설치, 소스 폴더 탐색, 빌드 명령, 별도 .NET 설치는 필요하지 않습니다. Windows x64 실행 환경을 설치 파일에 포함했습니다. 관리자 권한을 요구하지 않고 현재 사용자에게 설치합니다.

GitHub의 **Code → Download ZIP은 개발용 소스**이며 위 실행용 ZIP과 다릅니다. 소스 ZIP을 받았다면 압축 푼 폴더 최상단의 `Download-EduStream.url`을 더블클릭해 실행용 ZIP을 받을 수 있습니다.

## 앱 실행 후

- 교수자: **세션 열기 → WDS 공유 시작**.
- 학생: **설정 → 표시 이름/교수자 호스트/포트 → 세션 참여 → 별도로 받은 초대 비밀번호 입력 → RDP 연결**.
- 앱을 실행하는 것만으로 세션과 화면 공유를 자동 시작하지 않습니다.
- 같은 PC가 아니면 학생 호스트에 `127.0.0.1`이 아닌 교수자 PC의 주소를 입력합니다.
- 초대 비밀번호는 Windows 계정 비밀번호가 아니며 공용 채팅에 보내지 않습니다.

상세 접속/파일/채팅/종료는 [실행 가이드](./RUN_GUIDE.md)를 참조합니다.

## 갱신과 제거

- 새 버전은 GitHub에서 새 설치 파일을 받아 기존 설치 위치에 다시 설치합니다. 자동 업데이트 기능은 아직 없습니다.
- 실행 중인 강의는 먼저 정상 종료한 뒤 업데이트합니다.
- 설치 역할을 줄이거나 완전히 바꾸려면 기존 EduStream을 제거한 뒤 원하는 역할로 다시 설치합니다.
- 제거: **Windows 설정 → 앱 → 설치된 앱 → EduStream → 제거**.
- 앱과 설치 프로그램이 만든 바로가기는 제거되지만, 수신한 강의 파일은 제거하지 않습니다.
- 기본 설치 위치는 `%LOCALAPPDATA%\Programs\EduStream`이며, 사용자가 이 경로를 직접 찾아갈 필요는 없습니다.
- 이전 수동 생성 바로가기/실행본은 정식 설치 관리 대상이 아닙니다.

## 현재 배포 범위와 주의

- `0.1.0-preview.1`은 시험 배포판입니다. 실제 다중 PC RDP 품질/지연·15분 실사용·전체 시연 인수는 진행 중입니다.
- Windows WDS/COM 지원은 별도 OS 조건입니다. .NET 포함 설치가 WDS 지원까지 보장하지 않습니다.
- 현재 설치 파일과 ZIP의 실행 파일에 EduStream 코드 서명 인증서는 적용하지 않았습니다. Windows가 게시자를 확인할 수 없다고 알리거나 보안 정책으로 차단할 수 있습니다.
- 공식 저장소 Releases의 파일/해시를 확인하고 조직 정책으로 차단되면 관리자에게 문의합니다. 백신/방화벽 전체 해제나 조직 정책 우회를 안내하지 않습니다.

## 개발자 전용: 실행용 ZIP 만들기

빌드 PC에는 .NET SDK가 필요하지만 Inno Setup은 필요하지 않습니다.

```powershell
./scripts/Build-EduStreamPortable.ps1 -OutputDirectory "$PWD/artifacts/portable/new-build"
./scripts/Test-EduStreamPortable.ps1 -ZipPath "$PWD/artifacts/portable/new-build/EduStream-Portable-win-x64.zip"
```

- 두 self-contained 게시 결과를 하나의 폴더로 합치며, 같은 경로의 파일 내용이 다르면 덮어쓰지 않고 중단합니다.
- 최상단에 역할별 한글 EXE 이름을 배치합니다. ZIP과 별도 `.zip.sha256`을 Releases에 올립니다.
- 테스트는 해시, 한글/공백 경로의 실제 압축 해제, 포함 런타임과 RDP interop, 두 GUI 시작을 확인합니다. 새 Windows PC의 WDS 지원이나 실제 다중 PC 화면 공유를 대신 검증하지 않습니다.
- ZIP과 중간 payload는 Git에 커밋하지 않습니다. 설치판의 기존 해시 파일은 변경하지 않습니다.

## 개발자 전용: 설치 파일 만들기

배포받는 사용자가 수행하는 절차가 아닙니다. 빌드 PC에 .NET SDK와 [Inno Setup 6](https://jrsoftware.org/isdl.php)을 준비합니다.

```powershell
./scripts/Build-EduStreamInstaller.ps1 -Version '0.1.0-preview.1'
```

- Server/Client를 Windows x64 self-contained로 게시합니다. 현재 포함 런타임은 .NET 8.0.31로 고정합니다.
- 기존 출력 폴더가 있으면 덮어쓰지 않고 중단합니다. 재생성은 새 OutputDirectory를 지정합니다.
- 결과: `artifacts/installer/<버전>/EduStream-Setup.exe`. 이 **한 파일**을 Releases에 올립니다.
- `payload/`는 빌드 중간 산출물입니다. 사용자에게 소스 또는 payload 탐색을 요구하지 않습니다.
- `Publish-EduStreamDesktop.ps1`은 개발자 게시 보조 도구이고 사용자 설치 파일이 아닙니다.
- 배포 전 새 .NET 보안 패치와 Inno Setup 버전/서명을 확인합니다. Inno Setup의 상용 사용 조건은 공식 라이선스를 확인합니다.

격리된 Windows 계정에서 설치/재설치/제거를 검증합니다. 이미 EduStream이 설치돼 있으면 테스트가 중단되며 기존 설치를 덮어쓰지 않습니다.

```powershell
./scripts/Test-EduStreamInstaller.ps1 -InstallerPath '실제 EduStream-Setup.exe 경로'
```

테스트는 학생용/교수자용/둘 다 설치, 역할별 파일, .NET 포함, 시작 메뉴 바로가기, GUI 시작, 재설치 및 제거를 검증합니다. 기존 바탕화면 아이콘은 변경하지 않습니다. 실제 RDP 시연을 대신하지 않습니다.
