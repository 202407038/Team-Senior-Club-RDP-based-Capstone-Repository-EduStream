# EduStream

교수자와 학생이 같은 로컬 네트워크에서 화면 공유·판서·원격 제어·채팅·파일 배포를 함께 사용하는 Windows 강의 지원 프로그램입니다.

## 정식 버전 다운로드

### EduStream v1.1.2 — 역할별 다운로드

- **[교수자용 ZIP 받기](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v1.1.2/EduStream-Server-win-x64.zip)**
- **[학생용 ZIP 받기](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v1.1.2/EduStream-Client-win-x64.zip)**

각 ZIP에는 해당 역할의 앱만 들어 있습니다. 학생에게는 **학생용 ZIP만** 전달하세요. 두 앱 모두 v1.1.2입니다. 학생 파일 제출을 끌어 놓기 후 '제출하기'로 보내고, 교수자 쪽에는 원래 파일명으로 저장됩니다. 화면 문구와 안내도 쉬운 말로 정리했습니다.

**다운로드 → 모두 압축 풀기 → EduStream 폴더 → 역할에 맞는 EXE 더블클릭**

- 교수자 PC: `EduStream 교수자.exe`
- 학생 PC: `EduStream 학생.exe`

Windows x64용이며 .NET 실행 환경을 포함합니다. 설치 프로그램이나 명령어 입력은 필요하지 않습니다. ZIP 안에서 바로 실행하지 말고 전체 압축을 풀어주세요. EXE 옆의 DLL과 하위 폴더도 함께 유지해야 합니다.

[배포 정보·SHA-256](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/tag/v1.1.2) · [자세한 실행 안내](docs/work/DESKTOP_LAUNCH.md) · [변경 이력](CHANGELOG.md)

GitHub의 **Code → Download ZIP**과 **Source code (zip)**은 개발용 소스입니다. 프로그램 실행만 필요하면 위 **실행용 ZIP**을 받으세요. 설치형은 배포하지 않습니다.

## 처음 사용하기

1. 교수자는 방 비밀번호 사용 여부를 정한 뒤 **세션 열기**를 누릅니다. 필요하면 세션을 열기 전에 엑셀 학생 명단을 불러옵니다. 불러오면 명단 대조가 바로 켜집니다.
2. 학생은 **교수자 자동 찾기**에서 원하는 교수자를 선택하거나 교수자 화면에 표시된 IP를 입력합니다. 이름, 명단 대조 수업의 학번, 설정된 방 비밀번호를 입력하고 **세션 참여**를 누릅니다.
3. 교수자는 모니터를 선택하고 **공유 시작**을 누릅니다. 학생은 별도의 접속 코드나 RDP 초대 비밀번호를 입력하지 않습니다.

기본 포트는 5000이며 바꿀 때만 고급 설정을 사용합니다. 다른 PC에 접속할 때 `127.0.0.1`이 아닌 교수자 PC의 IP를 입력하세요. 교수자와 학생은 같은 버전을 사용합니다.

## 주요 기능

- **화면 공유·판서:** WDS(RDP) 실시간 공유, 모니터 선택, 선·도형·색·굵기·지우개·실행 취소, 그림 유지/OFF와 숨김 구분.
- **학생 화면·원격 제어:** 학생별 화면 펼치기·큰 보기와 교수자의 제어 대상 선택. 학생 보기를 열어도 교수자 공유를 자동 중지하지 않습니다.
- **채팅·공지:** 전체/교수자–학생 개인 대화, 복사, 미읽음 표시. 일반 공지는 약 3초 표시하고, 중요 공지는 확인할 때까지 유지하며 공지함에서 다시 확인합니다.
- **파일:** 교수자 세션 파일 등록·다운로드·등록 해제, 학생별 저장 현황, 학생 제출 열기/닫기·취소·재제출. 강의 파일은 Windows 다운로드 폴더에 저장합니다.
- **참가·명단:** 교수자 자동 검색·여러 결과 선택, 선택적 방 비밀번호와 엑셀 이름/학번 대조, 출입 기록 CSV.
- **사용 편의:** 참가 정보 기억, 라이트/다크 테마, 접이식 패널, 연결 실패 안내와 재시도.

[기능별 사용법](docs/work/ADDITIONAL_FEATURES_GUIDE.md)

## 사용 조건과 알려진 제한

- 같은 **신뢰할 수 있는 LAN**에서 사용합니다. 자동 찾기는 방 비밀번호·명단 대조를 생략하지 않으며 엑셀 대조는 학교 계정 본인 인증이 아닙니다.
- 학생은 참가 시 화면 보기·원격 제어 허용이 기본 ON입니다. 수업 전에 이를 안내하고, 학생은 **세션 나가기**로 연결을 종료할 수 있습니다.
- 한 PC의 동일 Windows 로그인에서 교수자·학생이 동시에 WDS 송신을 열면 인증 오류가 발생하는 조건이 있습니다. 양방향 수업은 **서로 다른 PC**에서 사용하세요. Windows WDS/COM 지원도 필요합니다.
- 교수자 창과 학생 보기 창은 **캡처 차단을 유지**합니다. 실패 안내가 나오면 해당 창을 공유하지 않는 모니터로 옮기세요. 모든 캡처 도구·OS에서의 차단을 보장하지는 않습니다.
- 휠 확대·축소는 제공하지 않습니다. 최대 동시 접속 인원은 실측 확정하지 않았으며 내부 초대 한도 50을 수용 인원으로 해석하지 않습니다.
- 코드 서명과 자동 업데이트는 제공하지 않습니다. 공식 릴리스의 파일/해시를 확인하고, 조직의 보안 정책을 우회하거나 백신·방화벽을 전체 해제하지 마세요.

정식 버전 표시는 배포 채널을 뜻하며 모든 PC 환경에 대한 무결함 보장은 아닙니다. [검증 범위와 현재 상태](docs/work/STATUS_AND_ROADMAP.md), [문제 보고 방법](docs/work/DESKTOP_LAUNCH.md#문제가-생기면)을 확인하세요.

## 개발 및 문서

소스를 확인하려면 `Code → Download ZIP`을 풀고 `EduStream.sln`을 Visual Studio에서 엽니다. 빌드는 .NET 8 SDK가 필요합니다.

```powershell
dotnet build EduStream.sln -c Release
dotnet test EduStream.sln -c Release --no-build
```

일부 테스트는 실제 창·WDS·입력을 사용합니다. 사용자 화면을 건드리지 않아야 하는 환경에서는 무조건 전체 실행하지 말고 [검증 범위](docs/work/FINAL_ACCEPTANCE_CHECKLIST.md)를 먼저 확인하세요.

`src`에는 교수자(Server)·학생(Client)·공통(Core)·화면 엔진(ShareHost/ShareViewer), `tests`에는 회귀 검사가 있습니다. 테스트 코드와 과거 검증 이력은 유지하며, 실제 학생 명단·출입 기록·개인 제출 자료는 공개 저장소에 넣지 않습니다.

- [문서 목록](docs/README.md) · [개발·커밋·PR 규칙](docs/work/DEVELOPMENT_GUIDE.md)
- [현재 상태와 로드맵](docs/work/STATUS_AND_ROADMAP.md) · [주차별 기록](docs/work/FULL_WEEKLY_ROADMAP.md)
- [아키텍처](docs/reference/ARCHITECTURE_GUIDE.md) · [작업 기록](docs/reference/PROJECT_HISTORY_TIMELINE.md)
