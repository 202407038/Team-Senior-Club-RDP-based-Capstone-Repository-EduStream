# EduStream

EduStream은 교수자 화면 공유, 파일 전송, 텍스트 채팅을 하나의 흐름으로 제공하는 WPF 기반 데스크톱 강의 보조 시스템입니다.

현재 저장소는 졸작 중간발표 PPT의 로드맵을 기준으로 기능 구현과 시연 준비를 진행합니다.

## 프로젝트 구성

- `EduStream.Server`: 교수자용 서버 앱
- `EduStream.Client`: 수강생용 클라이언트 앱
- `EduStream.Core`: 공통 패킷, 모델, 직렬화, 검증 규칙
- `EduStream.FileTransfer.Tests`: 파일 전송 및 패킷 흐름 테스트

## 실행 및 검증

### 빌드

```bash
dotnet build EduStream.sln
```

### 테스트

```bash
dotnet test EduStream.sln --no-build
```

## 작업자용 문서

작업자는 아래 두 문서를 우선 확인합니다.

- [현재 상태와 로드맵](docs/work/STATUS_AND_ROADMAP.md)
- [개발 작업 방식](docs/work/DEVELOPMENT_GUIDE.md)

## 참고 문서

- [아키텍처 가이드](docs/reference/ARCHITECTURE_GUIDE.md)
- [사용자 워크플로우](docs/reference/USER_WORKFLOW_SCENARIOS.md)
- [작업 기록](docs/reference/PROJECT_HISTORY_TIMELINE.md)
- [졸작 중간발표 자료](docs/reference/presentations/졸작중간발표.pptx)

## 문서 기준

- 현재 작업 방향은 졸작 중간발표 PPT의 월간 로드맵을 기준으로 합니다.
- 로드맵은 현재 달과 다음 달까지만 유지합니다.
- 완료된 작업은 로드맵 문서에서 `~~내용~~ 완료` 형식으로 표시합니다.
- 오래된 계획 문서는 `docs/archive`에 보관합니다.
