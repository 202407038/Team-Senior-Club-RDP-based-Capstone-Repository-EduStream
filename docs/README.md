# EduStream 문서 허브

이 디렉터리는 `README.md`에 모두 담기에는 길고, 협업 과정에서 자주 갱신될 문서를 분리해서 관리하기 위한 공간입니다.

프로젝트를 처음 받는 팀원은 아래 순서로 읽는 것을 권장합니다.

1. `README.md`
2. `docs/TEAM_DEVELOPMENT_GUIDE.md`
3. `docs/ARCHITECTURE_GUIDE.md`
4. `docs/IMPLEMENTATION_PLAYBOOK.md`

## 문서 목록

### 1. [팀 개발 가이드](./TEAM_DEVELOPMENT_GUIDE.md)

- 브랜치 전략
- 커밋 메시지 규칙
- Pull Request 작성 및 리뷰 절차
- 코딩 규칙
- 협업 중 충돌을 줄이기 위한 기본 원칙

### 2. [아키텍처 가이드](./ARCHITECTURE_GUIDE.md)

- 프로젝트 구조와 각 계층의 책임
- 프로젝트 간 참조 방향
- 서비스, ViewModel, 모델 분리 원칙
- 현재 사용 기술과 도입 예정 기술 설명

### 3. [구현 플레이북](./IMPLEMENTATION_PLAYBOOK.md)

- 5인 기준 권장 작업 분담
- 인원이 부족할 때의 축소 운영 방법
- 각 작업 단위를 시작하고 끝내는 절차
- 기능별 권장 개발 순서

## 문서 운영 원칙

- 설계가 바뀌면 UML과 코드만 바꾸지 말고 관련 문서도 함께 수정합니다.
- 구현 완료 전 항목은 "현재 사용 중"과 "도입 예정"을 구분해서 기록합니다.
- 브랜치 전략, 커밋 규칙, 의존성 규칙이 바뀌면 이 디렉터리의 문서를 먼저 갱신합니다.
