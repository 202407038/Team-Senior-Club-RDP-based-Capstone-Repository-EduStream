# 구현 플레이북

## 1. 목적

이 문서는 팀원이 병렬로 작업할 때 구현 범위를 어떻게 나누고, 어떤 파일을 주로 건드리며, 어떤 순서로 작업을 진행해야 충돌을 줄일 수 있는지를 정리한 상세 협업 가이드입니다.

이번 버전은 기존보다 더 촘촘한 구현 단계를 전제로 하며, 5명 개발 기준에서 각 담당자가 “무엇을 언제까지” 해야 하는지 더 구체적으로 안내합니다. 이번 스프린트는 기존 문서 대비 작업량을 2배 수준으로 확장하지만, 역할 분담과 소유 범위는 그대로 유지합니다.

## 2. 작업 시작 전 기본 절차

모든 개발 팀원은 작업 시작 전에 아래 순서로 원격 최신 상태를 맞춥니다.

```bash
git fetch --all --prune
git status --short --branch
```

그 다음 권장 순서:

```bash
git checkout main
git pull --ff-only origin main
git checkout -b feature/<작업명>
```

작업 종료 전에는 최소 한 번 이상 아래를 확인합니다.

```bash
dotnet build EduStream.sln
git status
```

현재 작업 브랜치가 원격보다 뒤처진 상태라면 아래를 먼저 실행합니다.

```bash
git pull --ff-only origin <현재브랜치>
```

## 3. 5명 기준 권장 작업 분담

### 1번 담당: 공통 코어 / 프로토콜

담당 범위:
- `src/EduStream.Core`
- 패킷 모델
- 직렬화기
- 공통 검증 유틸
- 응답/에러 코드

주요 파일:
- `Models/BasePacket.cs`
- `Models/PacketType.cs`
- `Models/ChatPacket.cs`
- `Models/FilePacket.cs`
- `Models/ScreenPacket.cs`
- `Serialization/PacketSerializer.cs`
- `Protocols/*`
- `Utils/*`

이번 스프린트에서 해야 하는 일:
- 패킷 계약 정리
- 파일/화면/세션 공통 규칙 정리
- 공통 팩토리/헬퍼 추가
- 서비스 계층이 중복 구현 중인 규칙을 코어로 끌어올리기
- 검증 규칙을 코드 상수와 문서 기준으로 동시에 남기기
- 새로 추가된 파일/화면 전송 계약을 다른 담당자가 바로 사용할 수 있게 예제 흐름까지 정리하기

작업 절차:
1. 현재 서비스 계층에서 중복되는 규칙을 찾는다.
2. 코어로 옮길 상수/유틸/팩토리를 분리한다.
3. 서버/클라이언트가 그 코어 규칙을 실제로 쓰도록 정리한다.
4. 바뀐 규칙을 스프린트 문서와 UML 기준으로 다시 확인한다.

### 2번 담당: 서버 세션 / 네트워크

담당 범위:
- `src/EduStream.Server/Services/SessionManager.cs`
- `src/EduStream.Server/Services/TcpServerService.cs`
- `src/EduStream.Server/Services/HeartbeatService.cs`

이번 스프린트에서 해야 하는 일:
- 세션 개설/종료
- 참가자 관리
- join / leave / disconnect 처리
- ack / error 반환
- heartbeat 흐름 유지
- 다중 참여자 기준 상태 갱신 시점 정리
- 재접속과 비정상 종료 로그 기준 정리

작업 절차:
1. 세션 열기/닫기 흐름을 먼저 안정화한다.
2. join / leave / disconnect 시 참가자 수 갱신과 로그를 연결한다.
3. ack / error 패킷이 UI에 반영되는지 확인한다.
4. heartbeat가 세션 상태와 충돌하지 않는지 확인한다.

### 3번 담당: 서버 화면 송신

담당 범위:
- `src/EduStream.Server/Services/ScreenCapturer.cs`
- `src/EduStream.Server/Services/RdpHost.cs`
- `src/EduStream.Core/Models/ScreenPacket.cs`
- 필요 시 `src/EduStream.Core/Utils/ScreenTransferUtility.cs`

이번 스프린트에서 해야 하는 일:
- 화면 프레임 생성
- 인코딩 규칙 확인
- ScreenPacket 메타데이터 채우기
- 서버 송신 흐름과 연결
- 프레임 실패 시 fallback 처리
- 다중 기능 동시 실행 중 화면 송신 상태 확인

작업 절차:
1. 화면 캡처 결과물이 실제 바이트 배열로 나오는지 먼저 확인한다.
2. `ScreenPacket`에 폭/높이/인코딩/프레임 길이를 정확히 채운다.
3. 캡처 실패 시 fallback 프레임을 정의한다.
4. 서버 ViewModel 또는 서비스에서 “보낼 수 있는 상태”까지 연결한다.

### 4번 담당: 파일 전송

담당 범위:
- `src/EduStream.Server/Services/FileDistributor.cs`
- `src/EduStream.Client/Services/FileReceiver.cs`
- `src/EduStream.Core/Models/FilePacket.cs`
- `src/EduStream.Core/Utils/FileTransferUtility.cs`

이번 스프린트에서 해야 하는 일:
- 청크 분할
- 메타데이터 검증
- 체크섬 검증
- 임시 저장 및 최종 저장
- 실패 처리
- 작은 파일과 중간 크기 파일 모두 검증
- 실패 케이스 로그와 UI 반영까지 연결

작업 절차:
1. 파일을 청크 단위로 나누는 규칙을 고정한다.
2. 각 청크 메타데이터를 검증하는 규칙을 적용한다.
3. 수신 측에서 누적/조립/검증/저장 흐름을 끝까지 만든다.
4. 실패 케이스를 최소 1개 이상 확인한다.

### 5번 담당: 클라이언트 UI / 수신 상태

담당 범위:
- `src/EduStream.Client/MainWindow.xaml`
- `src/EduStream.Client/ViewModels/ClientViewModel.cs`
- `src/EduStream.Client/Services/SessionClient.cs`
- 수신 상태와 연동되는 서비스

이번 스프린트에서 해야 하는 일:
- 세션 참가/이탈 UI
- 채팅 UI
- 파일 다운로드 상태 UI
- 화면 수신 상태 UI
- 에러/성공 메시지 표시
- 상태 패널 간 우선순위 정리
- 서버 응답 로그와 사용자 안내 문구를 분리해 표시

작업 절차:
1. ViewModel 상태값을 먼저 정리한다.
2. XAML은 Grid 기반으로 구역을 나눈다.
3. 세션 -> 채팅 -> 파일 -> 화면 순서로 UI를 쌓는다.
4. 서비스에서 받은 결과를 상태 텍스트와 로그에 반영한다.

## 4. 인원 부족 시 축소 운영

단, 5명 기준 기본 운영에서는 역할을 합치지 않습니다. 작업량을 2배로 늘리더라도 각 담당자의 책임 경계는 유지하고, 범위 확장은 담당 영역 안에서만 수행합니다.

### 4명일 때
- 1번 코어 담당 유지
- 2번 세션/네트워크 담당 유지
- 3번 화면 송신 담당 유지
- 4번 파일 전송 + 5번 클라이언트 UI를 묶어서 진행

### 3명일 때
- 1명: 공통 코어
- 1명: 서버 세션 + 화면 송신
- 1명: 파일 전송 + 클라이언트 UI

### 2명 이하일 때
우선순위를 아래 순서로만 진행합니다.

1. 공통 코어
2. 세션 흐름
3. 채팅
4. 파일 전송
5. 화면 송신

즉, 화면 송신은 가장 마지막입니다.

## 5. 기능별 권장 구현 순서

### 채팅

1. `ChatPacket` 필드 확인
2. 서버 수신/브로드캐스트 연결
3. 클라이언트 수신 처리
4. 서버 UI / 클라이언트 UI 반영
5. 송신자 이름 표시 보정

### 파일 전송

1. `FilePacket` 메타데이터 점검
2. 청크 크기 규칙 정리
3. 송신 메서드 구현
4. 수신 조립/검증 구현
5. 저장 결과/실패 상태를 UI에 반영

### 화면 송신

1. 캡처 방식 확인
2. `ScreenPacket` 메타데이터 정리
3. 프레임 생성
4. 서버에서 송신 가능 상태까지 연결
5. 클라이언트 수신 상태/UI 반영

## 6. 충돌을 줄이는 방법

- 같은 파일을 둘 이상이 동시에 오래 잡고 있지 않습니다.
- 구조 변경과 기능 구현은 분리합니다.
- UI 작업과 서비스 구조 변경이 동시에 필요하면, 먼저 ViewModel/메서드 시그니처를 합의합니다.
- PR 하나에 목적 하나만 담습니다.

## 7. PR 권장 단위

좋은 예:
- `feat: add shared packet factory to core`
- `feat: add chunked file transfer receive flow`
- `feat: add server screen preview packet flow`
- `feat: redesign client transfer status panels`

좋지 않은 예:
- 채팅 + 파일 + 화면 + UI 레이아웃을 한 번에 넣는 PR

## 8. 문서 반영 규칙

다음 상황에서는 문서를 함께 수정합니다.

- 공통 패킷 구조가 바뀔 때
- 새 프로토콜 상수/유틸이 추가될 때
- 브랜치 전략/협업 절차가 바뀔 때
- 스프린트 목표나 역할 분담이 바뀔 때

## 9. 최종 목표

각 담당자가 아래를 보면 현재 위치를 바로 이해할 수 있어야 합니다.

- `README.md`: 프로젝트 개요
- `docs/TEAM_DEVELOPMENT_GUIDE.md`: 브랜치/커밋/PR 규칙
- `docs/ARCHITECTURE_GUIDE.md`: 구조와 의존성
- `docs/IMPLEMENTATION_PLAYBOOK.md`: 실제 구현 범위와 절차
- `docs/SPRINT_7DAY_IMPLEMENTATION_GUIDE.md`: 이번 주 상세 일정
