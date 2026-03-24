# EduStream: RDP 기반 실시간 강의 화면 공유 시스템

EduStream은 교수자 화면을 여러 수강생에게 실시간으로 전달하고, 파일 전송 및 텍스트 채팅 기능을 함께 제공하는 WPF 기반 데스크톱 애플리케이션입니다.

현재 저장소는 서버, 클라이언트, 공통 코어 프로젝트의 기본 골격을 갖춘 초기 단계이며, 핵심 네트워크 및 스트리밍 기능은 순차적으로 구현할 예정입니다.

---

## 목차
1. [프로젝트 개요](#프로젝트-개요)
2. [현재 상태](#현재-상태)
3. [디렉터리 구조](#디렉터리-구조)
4. [UML 다이어그램](#uml-다이어그램)
5. [사용자 워크플로우](#사용자-워크플로우)
6. [시스템 구성](#시스템-구성)
7. [기술 스택](#기술-스택)
8. [도입 예정 기술](#도입-예정-기술)
9. [로드맵](#로드맵)
10. [시작하기](#시작하기)
11. [협업 가이드](#협업-가이드)

---

## 프로젝트 개요

- `EduStream.Server`: 교수자용 방송 애플리케이션
- `EduStream.Client`: 수강생용 수신 애플리케이션
- `EduStream.Core`: 서버와 클라이언트가 공유하는 공통 로직 및 프로토콜 계층

목표 기능은 다음과 같습니다.

- 실시간 화면 공유
- 파일 전송
- 텍스트 채팅
- 공통 프로토콜 기반 통신 구조 정리

---

## 현재 상태

현재 프로젝트는 다음 수준까지 준비되어 있습니다.

- 루트 솔루션 `EduStream.sln` 구성 완료
- WPF 기반 서버/클라이언트 프로젝트 생성 완료
- 공통 코어 라이브러리 프로젝트 생성 완료
- 기본 진입 창 및 애플리케이션 골격 구성 완료
- 공통 패킷 계약과 세션 흐름 스켈레톤 반영 완료

아래 기능은 아직 본격 구현 전입니다.

- 실제 TCP/UDP 통신
- 화면 캡처 및 스트리밍
- 실제 세션 join/leave 네트워크 연결
- 파일 청크 재조립 및 재전송
- Heartbeat 주기 관리

---

## 디렉터리 구조

```text
Team-Senior-Club-RDP-based-Capstone-Repository/
├─ assets/
├─ docs/
├─ src/
│  ├─ EduStream.Server/
│  │  ├─ EduStream.Server.csproj
│  │  └─ ...
│  ├─ EduStream.Client/
│  │  ├─ EduStream.Client.csproj
│  │  └─ ...
│  └─ EduStream.Core/
│     ├─ EduStream.Core.csproj
│     └─ ...
├─ BaseUML.mdj
├─ EduStream.sln
├─ LICENSE
└─ README.md
```

`EduStream.sln`이 루트 기준 메인 솔루션이며, `src/EduStream.Server/EduStream.Server.sln`은 서버 프로젝트 단독 실행용 보조 솔루션입니다.

---

## UML 다이어그램

루트의 `BaseUML.mdj`는 프로젝트 UML 문서 파일입니다.

- 권장 도구: `draw.io` 또는 `diagrams.net`
- 포함 탭: `Use Case`, `Class Diagram`, `Sequence`
- 수정 시 주의: draw.io에서 다시 열 수 있도록 XML 형식을 유지해야 합니다.

UML을 수정할 때는 현재 코드 구조와 다이어그램이 서로 어긋나지 않는지 함께 확인하는 것을 권장합니다.

---

## 사용자 워크플로우

현재 프로젝트 기준 대표 사용자 흐름은 "교수자의 실시간 강의 화면 공유 및 파일 배포" 시나리오입니다.

- 상세 문서: [사용자 워크플로우 시나리오](./docs/USER_WORKFLOW_SCENARIOS.md)
- 포함 내용:
  - 교수자 세션 개설
  - 수강생 세션 참여
  - 화면 공유
  - 파일 전송
  - 채팅
  - 세션 종료

README에는 요약만 두고, 상세 시나리오는 `docs/` 문서로 분리해 관리합니다.

---

## 시스템 구성

- `EduStream.Server`: 강의 세션 생성, 화면 송출, 파일 배포, 수강생 연결 관리 담당
- `EduStream.Client`: 교수자 화면 수신, 파일 다운로드, 채팅 수신 및 상호작용 담당
- `EduStream.Core`: 공통 모델, 패킷 구조, 네트워크 유틸리티, 프로토콜 정의 담당

---

## 기술 스택

- Language: C#
- Framework: .NET 8.0
- UI: WPF
- Solution: Visual Studio 2022 권장

현재 프로젝트 타깃 프레임워크는 다음과 같습니다.

- `EduStream.Server`: `net8.0-windows`
- `EduStream.Client`: `net8.0-windows`
- `EduStream.Core`: `net8.0`

---

## 도입 예정 기술

아래 항목은 설계상 후보로 검토 중인 기술이며, 현재 저장소에 아직 적용되지는 않았습니다.

- `MessagePack-CSharp`: 패킷 직렬화 경량화
- `MSTSCLib` 또는 RDP 연동 대안: 원격 세션 기능 검토
- 화면 캡처용 라이브러리 또는 Windows API 기반 구현

후보 기술은 실제 구현 방향이 확정되면 프로젝트 파일과 문서에 반영합니다.

---

## 로드맵

### Phase 1. 프로젝트 기반 정리

- 솔루션 구조 정리
- 서버/클라이언트 기본 UI 구성
- 공통 코어 계층 정리

### Phase 2. 통신 계층 구현

- 공통 패킷 모델 정의
- TCP/UDP 통신 구조 설계
- 세션 연결 및 해제 흐름 정의

### Phase 3. 실시간 기능 구현

- 화면 캡처 및 송신
- 클라이언트 수신 및 렌더링
- 채팅 기능 추가

### Phase 4. 부가 기능 구현

- 파일 전송
- 예외 처리 및 복구
- 성능 점검 및 다중 클라이언트 테스트

---

## 시작하기

### 사전 요구 사항

- Windows 10 또는 Windows 11
- .NET 8.0 SDK
- Visual Studio 2022

### 실행 방법

1. 저장소 클론

```bash
git clone https://github.com/CCSS0923/Team-Senior-Club-RDP-based-Capstone-Repository.git
```

2. 루트 솔루션 열기

```text
EduStream.sln
```

3. 시작 프로젝트 설정 후 실행

- 교수자 앱: `EduStream.Server`
- 수강생 앱: `EduStream.Client`

---

## 협업 가이드

상세 협업 문서는 `docs/` 아래 별도 문서로 관리합니다.

읽기 권장 순서:

1. [문서 허브](./docs/README.md)
2. [팀 개발 가이드](./docs/TEAM_DEVELOPMENT_GUIDE.md)
3. [아키텍처 가이드](./docs/ARCHITECTURE_GUIDE.md)
4. [구현 플레이북](./docs/IMPLEMENTATION_PLAYBOOK.md)
5. [사용자 워크플로우 시나리오](./docs/USER_WORKFLOW_SCENARIOS.md)

요약 규칙만 이 README에 남깁니다.

### 브랜치 전략 요약

- `main`: 항상 빌드 가능 상태 유지
- `feature/<name>`: 기능 개발
- `fix/<name>`: 버그 수정
- `docs/<name>`: 문서 작업
- `refactor/<name>`: 구조 개선

### 커밋 규칙 요약

- `feat`: 기능 추가
- `fix`: 버그 수정
- `docs`: 문서 수정
- `refactor`: 구조 개선
- `test`: 테스트 관련
- `style`: 스타일 및 포맷팅
- `chore`: 설정, 빌드, 의존성 관리

예시:

```text
feat: add session bootstrap flow
docs: add collaboration workflow guide
```

### 개발 원칙 요약

- UI 로직과 비즈니스 로직을 분리합니다.
- 공통 통신 모델은 `EduStream.Core`에 둡니다.
- 비동기 I/O는 `async`/`await`를 우선 사용합니다.
- 구현 완료 전 문서에는 "도입 예정"과 "현재 사용 중"을 구분해 적습니다.
- 상세 규칙은 `docs/` 문서를 기준으로 따릅니다.
