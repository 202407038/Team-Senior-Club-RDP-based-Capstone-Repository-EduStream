# EduStream

## ZIP을 풀고 바로 실행하기

### [실행용 ZIP 다운로드 · EduStream-Portable-win-x64.zip](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v0.1.0-preview.1/EduStream-Portable-win-x64.zip)

**다운로드 → 모두 압축 풀기 → EduStream 폴더 → `EduStream 교수자.exe` 또는 `EduStream 학생.exe` 더블클릭**

설치나 명령어 입력 없이 실행할 수 있으며 .NET 실행 환경이 포함되어 있습니다. ZIP 안에서 바로 실행하지 말고 전체 압축을 풀어주세요. EXE 옆의 DLL과 하위 폴더도 필요하므로, 이동할 때는 폴더 전체를 옮깁니다. [실행 안내](docs/work/DESKTOP_LAUNCH.md)

## 설치형을 원하는 경우

### [Windows 설치 파일 다운로드 · EduStream-Setup.exe](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/download/v0.1.0-preview.1/EduStream-Setup.exe)

**다운로드 → 설치 파일 더블클릭 → 학생용/교수자용 선택 → 설치 → 바탕화면 아이콘 실행**

일반 사용자는 PowerShell, 빌드 명령, 별도 .NET 설치가 필요하지 않습니다. [설치·실행 안내](docs/work/DESKTOP_LAUNCH.md)를 확인하세요.

현재 배포는 **Windows x64 시험판**입니다. 코드 서명은 미적용이며 다중 PC RDP 실사용 인수는 진행 중입니다. [배포 정보](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/releases/tag/v0.1.0-preview.1)

**위 실행용 ZIP과 `Code → Download ZIP`은 다릅니다.** `Code → Download ZIP`은 개발용 소스입니다. 소스 ZIP을 받은 경우 루트의 `Download-EduStream.url`에서도 실행용 ZIP을 받을 수 있습니다.

---

EduStream은 교수자 화면 공유, 파일 전송, 텍스트 채팅을 하나의 흐름으로 제공하는 WPF 기반 데스크톱 강의 보조 시스템입니다.

현재 저장소는 졸작 중간발표 PPT의 로드맵을 기준으로 기능 구현과 시연 준비를 진행합니다.

## 프로젝트 구성

- `EduStream.Server`: 교수자용 서버 앱
- `EduStream.Client`: 수강생용 클라이언트 앱
- `EduStream.Core`: 공통 패킷, 모델, 직렬화, 검증 규칙
- `EduStream.FileTransfer.Tests`: 파일 전송 및 패킷 흐름 테스트

## 실행 및 검증

자세한 실행 순서와 기본 설정은 [실행 및 설정 가이드](docs/work/RUN_GUIDE.md)를 확인합니다.

2026-09-15 기준 WDS 기반 RDP가 메인 화면 공유 경로입니다. 현 PC에서 실제 학생 viewer 2개와 파일·채팅 병행을 자동 검증했으며, 다중 PC 실시간 표시·15분 실사용·전체 시연 2회는 별도 인수 대상입니다. [현재 검증 범위](docs/work/SEPTEMBER_RDP_INTEGRATION.md)를 확인합니다.

### 빌드

```bash
dotnet build EduStream.sln
```

### 테스트

```bash
dotnet test EduStream.sln --no-build
```

## 작업자용 문서

작업자는 아래 문서를 우선 확인합니다.

- [실행 및 설정 가이드](docs/work/RUN_GUIDE.md)
- [현재 상태와 로드맵](docs/work/STATUS_AND_ROADMAP.md)
- [확정 UI 시안·피드백](docs/work/UI_FEEDBACK_SPEC.md) · [역할 배분·제출 일정](docs/work/UI_RDP_WORK_ALLOCATION.md) — 후속 구현 계획이며 현재 배포 기능과 구분합니다.
- [개발 작업 방식](docs/work/DEVELOPMENT_GUIDE.md)

## 참고 문서

- [아키텍처 가이드](docs/reference/ARCHITECTURE_GUIDE.md)
- [사용자 워크플로우](docs/reference/USER_WORKFLOW_SCENARIOS.md)
- [작업 기록](docs/reference/PROJECT_HISTORY_TIMELINE.md)
- [졸작 중간발표 자료](docs/reference/presentations/졸작중간발표.pptx)

## 문서 기준

- 현재 작업 방향은 졸작 중간발표 PPT의 월간 로드맵을 기준으로 합니다.
- 로드맵은 현재 달과 다음 달까지만 유지합니다.
- 완료된 작업은 로드맵 문서에서 `내용 ----- %완료%` 형식으로 표시합니다.
- 오래된 계획 문서는 `docs/archive`에 보관합니다.
