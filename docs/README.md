# EduStream 문서 허브

이 디렉터리는 `README.md`에 모두 넣기엔 길고, 작업 과정에서 자주 갱신되는 문서를 분리해 관리하기 위한 공간입니다.

프로젝트를 처음 받는 팀원은 아래 순서로 읽는 것을 권장합니다.

1. `README.md`
2. `docs/TEAM_DEVELOPMENT_GUIDE.md`
3. `docs/ARCHITECTURE_GUIDE.md`
4. `docs/IMPLEMENTATION_PLAYBOOK.md`
5. `docs/SPRINT_7DAY_IMPLEMENTATION_GUIDE.md`
6. `docs/USER_WORKFLOW_SCENARIOS.md`
7. `docs/PROJECT_HISTORY_TIMELINE.md`

## 문서 목록

### 1. [팀 개발 가이드](./TEAM_DEVELOPMENT_GUIDE.md)

- 브랜치 전략
- 커밋 메시지 규칙
- PR 작성 및 리뷰 기준
- 작업 시작 전 원격 동기화 절차
- 충돌 방지 규칙

### 2. [아키텍처 가이드](./ARCHITECTURE_GUIDE.md)

- 프로젝트 구조와 계층 책임
- 참조 방향
- ViewModel / Service / Model 분리 원칙
- 현재 사용 기술과 도입 예정 기술 설명

### 3. [구현 플레이북](./IMPLEMENTATION_PLAYBOOK.md)

- 5명 기준 상세 역할 분담
- 담당별 주요 파일
- 기능별 권장 구현 순서
- 인원 부족 시 축소 운영 방식

### 4. [사용자 워크플로우 시나리오](./USER_WORKFLOW_SCENARIOS.md)

- 교수자/수강생 기준 핵심 사용 시나리오
- 화면 공유, 파일 전송, 채팅, 세션 종료 흐름
- 향후 테스트 케이스 확장 기준

### 5. [7일 스프린트 상세 구현 가이드](./SPRINT_7DAY_IMPLEMENTATION_GUIDE.md)

- 5명 기준 7일 작업표
- 역할별 Day 1~7 상세 목표
- 기존 문서 대비 2배 확장된 작업량 기준
- 시연 가능한 데모를 목표로 한 구현 우선순위

### 6. [프로젝트 전체 작업 기록](./PROJECT_HISTORY_TIMELINE.md)

- 프로젝트 시작일부터 현재까지의 날짜 기반 작업 기록
- 파트별 라벨로 분류한 전체 변경 흐름
- `main` 히스토리와 최신 오픈 PR 기준 요약

## 문서 운영 원칙

- 공통 규칙이 바뀌면 `TEAM_DEVELOPMENT_GUIDE.md`를 먼저 수정합니다.
- 역할 분담과 구현 순서가 바뀌면 `IMPLEMENTATION_PLAYBOOK.md`를 수정합니다.
- 주간 목표와 일정이 바뀌면 `SPRINT_7DAY_IMPLEMENTATION_GUIDE.md`를 수정합니다.
- UML/코드/문서가 어긋나면 반드시 함께 정리합니다.
