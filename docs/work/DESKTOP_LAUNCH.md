# EduStream 실행용 ZIP 안내

현재 사용자 배포는 **v1.0.0 정식 실행용 ZIP만** 제공합니다(2026-11-09). 소스 기준은 `v1.0.0` 태그입니다. 기존 preview 릴리스는 과거 이력으로 보존합니다.

## 다운로드와 실행

1. 역할에 맞게 **[교수자용 ZIP](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v1.0.0/EduStream-Server-win-x64.zip)** 또는 **[학생용 ZIP](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v1.0.0/EduStream-Client-win-x64.zip)**을 받습니다. 학생에게는 학생용만 전달합니다.
2. 받은 ZIP을 우클릭 → **모두 압축 풀기 → 압축 풀기**.
3. `EduStream` 폴더의 **EduStream 교수자.exe / EduStream 학생.exe**를 더블클릭합니다.

Windows x64용 .NET 실행 환경이 포함돼 설치·PowerShell·별도 .NET 설치가 필요하지 않습니다. ZIP 안에서 직접 실행하지 마세요. DLL과 하위 폴더를 지우거나 EXE만 옮기지 말고 폴더 전체를 옮기세요. 바탕화면에는 EXE의 바로가기를 만들 수 있습니다.

GitHub **Code → Download ZIP** 및 Releases의 **Source code (zip)**은 개발용 소스입니다. 교수자용은 `EduStream-Server-win-x64.zip`, 학생용은 `EduStream-Client-win-x64.zip`이며 각 ZIP에는 해당 역할의 앱만 있습니다. 소스 루트의 `Download-EduStream-Professor.url` / `Download-EduStream-Student.url`은 각각 바로 다운로드하며 `Download-EduStream.url`은 역할을 선택할 수 있는 릴리스 안내로 연결합니다.

## 앱 실행 후

1. 교수자를 먼저 실행합니다. 같은 Windows 로그인 환경에서 학생이 먼저 켜져 있으면 교수자 실행이 제한됩니다.
2. 교수자는 방 비밀번호 사용 여부를 정하고 **세션 열기**를 누릅니다. 화면에 표시된 교수자 IP를 학생에게 알려줍니다. 공유할 모니터를 고른 뒤 **공유 시작**을 누릅니다.
3. 학생은 참가 화면에서 교수자를 자동으로 찾거나 IP를 입력합니다. 명단 대조를 켠 경우 이름·학번을 입력하고, 방 비밀번호가 있는 경우 비밀번호를 입력한 뒤 **세션 참여**를 누릅니다. 기본 포트는 5000입니다. **접속 코드 입력은 없습니다.**
4. 참가 후 강의 화면이 열리고 RDP 초대 비밀번호는 내부 보호 채널로 전달됩니다. 별도 RDP 연결 버튼이나 초대 비밀번호 입력은 필요하지 않습니다.
5. 공유 중지/재시작 시 방 참가는 유지하고 새 초대로 자동 연결을 시도합니다. 학생의 명시적 퇴장/교수자 세션 종료 뒤에는 자동 재참가하지 않습니다. 실패 안내가 나오면 해당 화면의 재시도 버튼을 사용하며 인증 오류를 무한 반복하지 않습니다.
6. 교수자는 파일 선택/드롭으로 파일을 고른 뒤 **등록 버튼**을 눌러 세션 목록에 등록합니다. 드롭만으로 자동 등록하지 않으며 한 번에 한 파일을 처리합니다. 학생은 파일 영역에서 파일명 또는 다운로드 버튼으로 받습니다. Windows 다운로드 폴더에 저장하며 등록 해제는 이미 받은 파일을 삭제하지 않습니다. **즉시 전송도 다운로드 폴더**에 저장하고 기존 동명 파일은 보존하며 새 파일 이름에 번호를 붙입니다.
7. 교수자는 공유 중 **판서 ON**으로 선택 모니터 위에 그립니다. 그림 유지 OFF, 숨김/표시, 전체 지우기, 지우고 OFF를 구분합니다. 도구창 X는 그림을 유지하고 그리기 OFF/도구창 숨김을 수행합니다.
8. 오른쪽 학생 이름을 펼치면 화면 수신을 시도합니다. 여러 목록은 독립적으로 펼치며 큰 보기는 기존 뷰어를 이동합니다. **학생 보기를 열어도 교수자 공유를 자동 중지하지 않습니다.** 캡처 제외 설정 실패 시 안내하며 해당 창을 공유하지 않는 모니터로 옮깁니다. API 성공만으로 모든 환경에서 재공유 방지가 보장되지는 않습니다.
9. 합의한 UI 간소화로 학생 허용 토글·즉시 제어 중단 버튼·로그 영역은 제거됐습니다. 보기·제어 허용은 기본 ON이며 세션 나가기는 유지합니다. 휠 확대·축소는 이번 범위에서 제외됐습니다.

다른 PC에서는 호스트 주소에 `127.0.0.1`이 아니라 교수자 PC의 로컬 네트워크 주소를 씁니다. 학생과 교수자는 같은 버전을 사용하세요. 상세 네트워크 조건과 실패 대응은 [실행 가이드](./RUN_GUIDE.md)를 참조합니다.

## 갱신과 정리

- 기존 앱과 강의를 정상 종료한 뒤 새 ZIP을 **새 폴더**에 풉니다. 자동 업데이트는 제공하지 않습니다.
- 받은 강의 자료를 보관한 뒤 이전 실행 폴더를 정리합니다. 실행 파일이 다른 폴더로 옮겨졌다면 직접 만든 바로가기도 새 위치로 변경합니다.
- 새 ZIP을 받았다고 이전 설치형이 자동 제거되지는 않습니다. 과거 설치형을 사용하는 경우 Windows 설정의 설치된 앱에서 EduStream을 제거할 수 있습니다.

## 검증 범위와 주의

- 비화면 선별 회귀 973개 통과·실패 0. 교수자 공유 중지 안내와 전체 채팅에서의 학번 비전달을 포함합니다. native/창/입력 클래스는 제외합니다.
- ZIP의 SHA-256은 [정식 릴리스](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/tag/v1.0.0)의 같은 파일명에 `.sha256`이 붙은 파일과 대조합니다. 각 ZIP에 .NET 8.0.31 self-contained 구성과 RDP interop를 포함합니다.
- 압축 해제본의 양 앱 시작과 교수자 창 캡처 제외 값 `0x11`을 확인했습니다. 이 시작 검사는 화면 전송이나 실제 원격 제어 검사가 아닙니다.
- 사용자의 양방향 공유·원격 제어·동시 접속 현장 확인은 별도입니다. 동일 Windows 로그인에서 동시 WDS 송신 제약이 있으며, 최대 동시 접속 인원은 보장하지 않습니다. [현재 검증 근거](./STATUS_AND_ROADMAP.md)를 확인하세요.
- Windows WDS/COM 지원은 별도 OS 조건이며 .NET 포함만으로 보장되지 않습니다.
- 코드 서명은 미적용입니다. 공식 Releases의 파일/해시를 확인하고 조직 정책 차단 시 관리자에게 문의하세요. 백신/방화벽 전체 해제나 정책 우회는 하지 마세요.

## 개발자: 배포 순서

정식 버전의 앱 버전은 `src/Directory.Build.props`에서 관리합니다. Git 태그 `v1.0.0`과 실행 파일의 제품 버전 `1.0.0`을 대조하고, 릴리스는 사전 릴리스가 아닌 Latest로 지정합니다. 과거 preview 태그·릴리스를 덮어쓰거나 삭제하지 않습니다.

**담당 작업 반영 → 보완·통합 → 빌드/테스트 → ZIP 생성·실행 검증·GitHub 배포 → README/바로가기/실행 안내와 로드맵·진척 최종 갱신** 순서를 따릅니다. 문서 오타 수정마다 앱을 재배포하라는 뜻은 아닙니다. 실행 코드 SHA, 배포 버전, 검증 범위를 기록하고 ZIP 배포 전에는 전체 업데이트 완료로 표시하지 않습니다.

```powershell
./scripts/Build-EduStreamPortable.ps1 -OutputDirectory "$PWD/artifacts/portable/new-build"
./scripts/Test-EduStreamPortable.ps1 -ZipPath "$PWD/artifacts/portable/new-build/EduStream-Server-win-x64.zip" -Role Server -PayloadDirectory "$PWD/artifacts/portable/new-build/payload"
./scripts/Test-EduStreamPortable.ps1 -ZipPath "$PWD/artifacts/portable/new-build/EduStream-Client-win-x64.zip" -Role Client -PayloadDirectory "$PWD/artifacts/portable/new-build/payload"
```

빌드 PC만 .NET SDK가 필요합니다. 각 self-contained 게시 폴더 전체를 역할별로 포장하고 다른 역할의 앱이 섞이면 중단합니다. 학생용 ShareHost/ShareViewer는 역방향 공유에 필요하므로 제거하지 않습니다. 이미 검증한 publish 결과는 빌드 스크립트의 `-PayloadDirectory`로 재사용할 수 있으며 원본 해시를 검사합니다. 역할별 ZIP 두 개와 각각의 `.zip.sha256`을 Releases에 배포하고 중간 payload/바이너리는 Git에 커밋하지 않습니다. 기존 통합 ZIP 링크는 호환용으로 보존하지만 신규 안내는 역할별 ZIP만 사용합니다. 과거 설치형 제작 스크립트는 이력 보존용입니다.

## 문제가 생기면

- 버전(실행용 ZIP v1.0.0), 교수자/학생 PC 구분, Windows 버전, 참가 인원, 공유·참가 순서, 오류 시각을 남깁니다.
- 기대 동작과 실제 결과, 오류 메시지를 함께 [GitHub Issues](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/issues)에 알려주세요. 비밀번호·초대 비밀·학번·사적인 화면은 가립니다.
- 캡처 차단 창을 찍기 위해 보호 설정을 해제하지 않습니다. 오류 문구와 재현 순서만으로도 보고할 수 있습니다.
- 업데이트 직후 반복되는 새 결함이면 수업/앱을 정상 종료하고 받은 자료를 보존합니다. 필요하면 보관된 직전 preview.8을 별도 폴더에서 양쪽 같은 버전으로 사용하고, v1.0.0 파일을 섞거나 사용자 자료를 덮어쓰지 않습니다. 이전 버전에서도 동일한 WDS 환경 제약은 존재합니다.
