# 팀 개발 가이드

## 1. 목적

이 문서는 EduStream 팀이 같은 기준으로 브랜치를 만들고, 커밋하고, 리뷰하고, 머지하기 위한 공통 규칙을 정리한 문서입니다.

이번 버전에서는 기존보다 더 촘촘한 스프린트 운영을 전제로 하며, 작업 시작 전 원격 동기화와 역할별 PR 분리를 더 강하게 요구합니다. 이번 7일 스프린트는 기존 문서 대비 작업량을 2배 수준으로 잡되, 역할 분담 자체는 유지합니다.

## 2. 작업 시작 전 필수 절차

모든 개발 팀원은 작업을 시작하기 전에 아래 순서로 원격 최신 상태를 확인합니다.

```bash
git fetch --all --prune
git status --short --branch
```

그 다음 기본 절차:

```bash
git checkout main
git pull --ff-only origin main
git checkout -b feature/<작업명>
```

이 절차를 생략하면 안 됩니다.

이유:
- 이미 머지된 PR을 놓치지 않기 위해
- 오래된 기준으로 개발하는 일을 줄이기 위해
- 브랜치 충돌과 중복 구현을 줄이기 위해

## 3. 브랜치 전략

### 기본 브랜치
- `main`
  - 항상 빌드 가능한 상태를 유지합니다.
  - 직접 커밋하지 않고 PR을 통해서만 반영합니다.

### 작업 브랜치
- `feature/<name>`
  - 기능 추가 또는 흐름 구현
- `fix/<name>`
  - 버그 수정, 안정성 보강
- `docs/<name>`
  - 문서 작업
- `refactor/<name>`
  - 구조 개선, 기능 변화 없음

예시:
- `feature/chat-flow`
- `feature/file-transfer`
- `fix/session-leave-race`
- `docs/sprint-update`

### 브랜치 생성 규칙

1. 항상 최신 `main`에서 분기합니다.
2. 한 브랜치에는 한 목적만 담습니다.
3. 역할이 다르면 브랜치도 분리합니다.
4. UI 작업과 코어 작업을 한 브랜치에 섞지 않습니다.

## 4. 커밋 메시지 규칙

형식:

```text
<type>: <summary>
```

사용 가능한 타입:
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
feat: add shared packet factory to core
fix: stabilize session join and leave flow
docs: expand 7-day sprint implementation guide
refactor: split screen packet validation helpers
```

커밋 원칙:
- 하나의 커밋에는 하나의 목적만 담습니다.
- 빌드가 깨진 상태로 커밋하지 않습니다.
- `tmp`, `test`, `asdf` 같은 임시 커밋 메시지는 금지합니다.
- 문서만 바꾸면 `docs`, 실제 동작이 바뀌면 `feat` 또는 `fix`를 씁니다.

## 5. PR 작성 규칙

### PR 생성 전 체크리스트
- `dotnet build EduStream.sln` 성공
- 불필요한 파일 없음
- 변경 목적이 제목과 본문에 드러남
- 문서/UML과 충돌 없는지 확인

### PR 제목

커밋 메시지 형식을 그대로 사용합니다.

예:

```text
feat: add client transfer status panels
```

### PR 본문에 들어가야 할 내용
- 작업 목적
- 주요 변경 사항
- 기대 효과 또는 해결한 문제
- 검증 방법
- 포함 파일 또는 변경 영역

### PR 분리 원칙

다음은 분리합니다.
- 코어 변경 PR
- 서버 세션 PR
- 서버 화면 송신 PR
- 파일 전송 PR
- 클라이언트 UI PR

한 PR에 넣지 않는 것:
- 채팅 + 파일 + 화면 + UI 전체
- 문서 + 구조 리팩터링 + 기능 구현
- 코어 계약 변경 + 대규모 UI 개편

## 6. 리뷰 기준

리뷰는 아래 순서로 확인합니다.

1. 빌드가 되는가
2. 책임 분리가 맞는가
3. 기존 문서와 충돌하지 않는가
4. 임시 코드가 영구 코드처럼 남아 있지 않은가
5. 다음 작업자가 이어받기 쉬운가

특히 확인할 것:
- `EduStream.Core`에 서버 전용 코드가 들어가 있지 않은가
- ViewModel에 네트워크/파일 처리 로직이 과도하게 섞여 있지 않은가
- 서비스 계층이 UI 상태를 직접 조작하지 않는가
- 코어 계약 변경 시 서비스/문서/UML 반영이 같이 됐는가

## 7. 코딩 규칙

### 공통
- `nullable enable` 전제를 유지합니다.
- 비동기 메서드는 `Async` 접미사를 붙입니다.
- 공개 메서드는 역할이 드러나는 이름을 사용합니다.
- 중복 상수와 문자열은 코어/프로토콜 계층으로 끌어올립니다.

### 주석
- 구현 의도, 임시 처리 이유, 한계는 한국어 주석으로 남깁니다.
- 코드만 읽어도 당연한 내용은 주석으로 반복하지 않습니다.

좋은 예:

```csharp
// 현재 단계에서는 체크섬 불일치 시 재전송 대신 저장 실패로 처리합니다.
```

나쁜 예:

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
- 기능이 커지면 헬퍼/팩토리/유틸로 더 쪼갭니다.

## 8. 일일 작업 절차

1. `git fetch --all --prune`
2. `git status --short --branch`
3. 최신 `main` 확인 또는 현재 브랜치 원격 차이 확인
4. 작업 브랜치 확인 또는 새 브랜치 생성
5. 오늘 작업 범위 재확인
6. 구현
7. 로컬 빌드
8. 커밋
9. 푸시
10. PR 생성

추가 원칙:
- 작업 시작 전에 현재 브랜치가 원격보다 뒤처졌다면 먼저 `git pull --ff-only origin <현재브랜치>`로 동기화합니다.
- 문서 작업도 예외 없이 원격 최신 상태를 맞춘 뒤 시작합니다.

## 9. 충돌 방지 규칙

- 같은 파일을 여러 명이 동시에 오래 점유하지 않습니다.
- 구조 변경 PR과 기능 구현 PR을 분리합니다.
- UI 개편은 ViewModel/서비스 시그니처와 먼저 합의합니다.
- 이미 PR이 열린 구조 변경 위에 얹는 작업은 그 브랜치를 기준으로 이어가거나, 머지 후 작업합니다.

## 10. 머지 기준

아래 조건이 만족되면 `main`에 머지할 수 있습니다.

- 빌드 성공
- 변경 목적 명확
- 불필요한 산출물 없음
- 역할 범위가 과도하게 섞이지 않음
- 리뷰에서 치명적인 리스크 없음

## 11. 관련 문서

- `README.md`
- `docs/ARCHITECTURE_GUIDE.md`
- `docs/IMPLEMENTATION_PLAYBOOK.md`
- `docs/SPRINT_7DAY_IMPLEMENTATION_GUIDE.md`
