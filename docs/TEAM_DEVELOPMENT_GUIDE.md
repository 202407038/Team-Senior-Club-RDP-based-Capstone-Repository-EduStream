# 팀 개발 가이드

## 1. 목적

이 문서는 EduStream 팀이 같은 기준으로 브랜치를 만들고, 커밋하고, PR을 작성하고, 리뷰하고, 머지하기 위한 공통 규칙을 정리한 문서입니다.

이번 버전에서는 작업 시작 전 동기화 절차와 브랜치/PR 분리 기준을 더 강하게 명시했습니다. 스프린트 작업량이 커졌기 때문에, 개발 흐름을 느슨하게 두면 충돌과 중복 구현이 바로 늘어납니다.

## 2. 작업 시작 전 필수 절차

모든 개발 팀원은 작업 시작 전에 아래 명령을 반드시 먼저 실행합니다.

```bash
git fetch --all --prune
git status --short --branch
git checkout main
git pull --ff-only origin main
```

그 다음 새 브랜치를 만듭니다.

```bash
git checkout -b feature/<작업명>
```

이미 작업 브랜치가 있다면 아래 명령으로 먼저 최신화합니다.

```bash
git pull --ff-only origin <현재브랜치>
```

이 절차는 예외 없이 적용합니다.

## 3. 브랜치 전략

### 기본 브랜치

- `main`
  - 항상 빌드 가능한 상태를 유지합니다.
  - 직접 커밋하지 않습니다.

### 작업 브랜치

- `feature/<name>`
  - 기능 구현
- `fix/<name>`
  - 버그 수정, 안정성 보강
- `docs/<name>`
  - 문서 작업
- `refactor/<name>`
  - 구조 정리, 기능 변화 없음

예시:
- `feature/chat-flow`
- `feature/server-screen-share-loop`
- `fix/session-leave-race`
- `docs/rewrite-sprint-guides`

### 브랜치 분리 원칙

- 역할이 다르면 브랜치도 다르게 쪼갭니다.
- 코어 변경과 UI 변경은 같은 브랜치에 넣지 않습니다.
- 문서 수정은 코드 변경 브랜치에 섞지 않습니다.
- 하나의 브랜치는 하나의 명확한 목적만 가집니다.

## 4. 커밋 메시지 규칙

형식:

```text
<type>: <summary>
```

사용 타입:
- `feat`
- `fix`
- `docs`
- `refactor`
- `test`
- `style`
- `chore`
- `revert`

예시:

```text
feat: add continuous server screen share loop
fix: stabilize session join and leave flow
docs: rewrite sprint implementation guides
refactor: apply packet factory to session flows
```

원칙:
- 한 커밋에는 한 목적만 넣습니다.
- 빌드가 깨진 상태로 커밋하지 않습니다.
- 임시 메시지(`tmp`, `test`, `asdf`)는 금지합니다.

## 5. PR 작성 규칙

### PR 생성 전 체크리스트

- `dotnet build EduStream.sln` 성공
- 불필요한 파일 없음
- 변경 목적이 제목과 본문에 드러남
- 문서/UML과 충돌하지 않음

### PR 제목

커밋 메시지 형식을 그대로 사용합니다.

### PR 본문에 들어갈 내용

- 작업 목적
- 주요 변경 사항
- 기대 효과 또는 해결한 문제
- 검증 방법
- 포함 파일 또는 영향 범위

### PR 분리 원칙

아래는 분리합니다.

- 코어 PR
- 세션/네트워크 PR
- 화면 송신 PR
- 파일 전송 PR
- 클라이언트 UI PR
- 문서 PR

좋지 않은 예:
- 채팅 + 파일 + 화면 + UI 전체를 한 번에 묶은 PR
- 문서 정리 + 대규모 리팩터링 + 기능 구현을 한 번에 넣은 PR

## 6. 리뷰 기준

리뷰는 아래 순서로 봅니다.

1. 빌드가 되는가
2. 책임 분리가 맞는가
3. 기존 문서/UML과 충돌하지 않는가
4. 임시 코드가 영구 코드처럼 들어가 있지 않은가
5. 다음 작업자가 이어받기 쉬운가

특히 확인할 것:
- `EduStream.Core`에 서버 전용 코드가 들어가지 않았는가
- ViewModel에 과도한 네트워크/파일 처리 로직이 들어가지 않았는가
- 서비스 계층이 UI를 직접 제어하지 않는가

## 7. 코딩 규칙

### 공통

- `nullable enable` 전제를 유지합니다.
- 비동기 메서드는 `Async` 접미사를 붙입니다.
- 공개 메서드는 역할이 드러나는 이름을 사용합니다.
- 중복 상수와 문자열은 코어/프로토콜 계층으로 올립니다.

### 주석

- 구현 의도, 임시 처리 이유, 한계점은 한국어 주석으로 남깁니다.
- 코드만 읽어도 obvious한 내용은 반복 설명하지 않습니다.

좋은 예:

```csharp
// 현재 단계에서는 체크섬 불일치를 즉시 전송 실패로 처리합니다.
```

좋지 않은 예:

```csharp
// 변수에 값을 넣습니다.
```

### UI / ViewModel

- XAML은 레이아웃과 바인딩 중심으로 유지합니다.
- 코드비하인드는 최소화합니다.
- 상태, 명령, 로그는 ViewModel로 보냅니다.

### 서비스 계층

- 세션, 파일, 화면, 직렬화, 네트워크는 각자 역할대로 분리합니다.
- 서비스는 UI를 직접 몰라야 합니다.
- 기능이 커지면 팩토리/유틸/검증 계층으로 내려줍니다.

## 8. 하루 작업 절차

1. 원격 동기화
2. 작업 브랜치 확인 또는 새 브랜치 생성
3. 오늘 작업 범위 확인
4. 구현
5. 로컬 빌드
6. 커밋
7. 푸시
8. PR 생성

## 9. 충돌 방지 규칙

- 같은 파일을 여러 명이 오래 잡지 않습니다.
- 구조 변경 PR과 기능 구현 PR은 가능하면 분리합니다.
- UI 개편이 필요하면 먼저 ViewModel/상태 구조를 합의합니다.
- 이미 열린 PR과 겹치는 작업은 먼저 기준 브랜치를 맞춘 뒤 진행합니다.

## 10. 머지 기준

아래 조건을 만족하면 `main`에 머지할 수 있습니다.

- 빌드 성공
- 변경 목적 명확
- 불필요한 산출물 없음
- 범위 과도하지 않음
- 리뷰에서 치명적 이슈 없음

## 11. 관련 문서

- `README.md`
- `docs/ARCHITECTURE_GUIDE.md`
- `docs/IMPLEMENTATION_PLAYBOOK.md`
- `docs/SPRINT_7DAY_IMPLEMENTATION_GUIDE.md`
