# 구현 플레이북

## 1. 목적

이 문서는 5명 개발 체제 기준으로 각 담당이 어떤 파일을 주로 건드리고, 어떤 순서로 구현을 진행하면 충돌을 줄이면서 속도를 낼 수 있는지 정리한 실무용 가이드입니다.

이번 버전은 기존보다 작업량을 더 촘촘하게 반영했습니다. 역할은 그대로 두되, 각 역할이 담당 범위 안에서 더 많은 산출물을 책임지는 방식으로 정리했습니다.

## 2. 작업 시작 기본 절차

모든 개발 팀원은 아래 순서를 먼저 수행합니다.

```bash
git fetch --all --prune
git status --short --branch
git checkout main
git pull --ff-only origin main
git checkout -b feature/<작업명>
```

작업 종료 전에는 최소한 아래 두 명령을 확인합니다.

```bash
dotnet build EduStream.sln
git status
```

## 3. 5명 기준 상세 역할 분담

### 1번. 공통 코어 / 프로토콜 담당

주요 파일:
- `src/EduStream.Core/Models/*`
- `src/EduStream.Core/Factories/*`
- `src/EduStream.Core/Protocols/*`
- `src/EduStream.Core/Utils/*`
- `src/EduStream.Core/Serialization/*`

주요 책임:
- 패킷 구조와 공통 상수 정리
- 공통 팩토리와 검증 유틸 정리
- 서버/클라이언트가 공유해야 하는 규칙을 코어로 이동
- 코어 관련 테스트 추가

작업 절차:
1. 서비스 계층에서 중복 구현되는 규칙을 찾는다.
2. 코어에 상수/유틸/팩토리/모델로 옮긴다.
3. 서버/클라이언트가 그 규칙을 사용하도록 치환한다.
4. 관련 테스트를 추가한다.
5. 문서와 UML에서 계약이 어긋나지 않는지 확인한다.

### 2번. 서버 세션 / 네트워크 담당

주요 파일:
- `src/EduStream.Server/Services/SessionManager.cs`
- `src/EduStream.Server/Services/TcpServerService.cs`
- `src/EduStream.Server/Services/HeartbeatService.cs`
- `src/EduStream.Server/ViewModels/ServerViewModel.cs`

주요 책임:
- 세션 open / close
- join / leave / disconnect
- 참가자 수와 참가자 목록 동기화
- heartbeat와 서버 로그 정리

작업 절차:
1. 세션 생명주기 메서드를 먼저 안정화한다.
2. 참가자 변경 이벤트를 ViewModel과 연결한다.
3. ack / error 응답을 코어 기준으로 정리한다.
4. disconnect / 비정상 종료를 테스트한다.
5. 서버 UI와 로그에서 상태가 읽히는지 확인한다.

### 3번. 서버 화면 송신 담당

주요 파일:
- `src/EduStream.Server/Services/ScreenCapturer.cs`
- `src/EduStream.Server/Services/ScreenShareService.cs`
- `src/EduStream.Server/Services/RdpHost.cs`
- `src/EduStream.Server/ViewModels/ServerViewModel.cs`
- `src/EduStream.Core/Models/ScreenPacket.cs`
- `src/EduStream.Core/Utils/ScreenTransferUtility.cs`

주요 책임:
- 실제 프레임 생성
- 프레임 메타데이터 정리
- 반복 송신 루프
- 송신 실패 fallback
- 서버 UI 상태 노출

작업 절차:
1. 캡처 설정과 인코딩 규칙을 정리한다.
2. 실제 프레임 생성 로직을 확인한다.
3. `ScreenPacket` 메타데이터를 일관되게 채운다.
4. 반복 송신 루프와 시작/중지 제어를 만든다.
5. 서버 UI에서 최신 상태가 보이게 한다.

### 4번. 파일 전송 담당

주요 파일:
- `src/EduStream.Server/Services/FileDistributor.cs`
- `src/EduStream.Client/Services/FileReceiver.cs`
- `src/EduStream.Core/Models/FilePacket.cs`
- `src/EduStream.Core/Utils/FileTransferUtility.cs`

주요 책임:
- 청크 단위 파일 송신
- 메타데이터 검증
- 체크섬 검증
- 최종 저장
- 실패 케이스 처리

작업 절차:
1. 청크 생성 규칙을 정한다.
2. 송신 메서드를 만든다.
3. 수신 버퍼링과 조립 로직을 만든다.
4. 체크섬과 메타데이터를 검증한다.
5. 성공/실패 각각 UI와 로그에 반영한다.

### 5번. 클라이언트 UI / 수신 담당

주요 파일:
- `src/EduStream.Client/MainWindow.xaml`
- `src/EduStream.Client/ViewModels/ClientViewModel.cs`
- `src/EduStream.Client/Services/SessionClient.cs`
- `src/EduStream.Client/Services/FileReceiver.cs`
- `src/EduStream.Client/Services/ScreenRenderer.cs`

주요 책임:
- 세션 상태 UI
- 채팅 UI
- 파일 수신 상태 UI
- 화면 수신 상태 UI
- 에러/성공 메시지 표현

작업 절차:
1. ViewModel 상태값을 먼저 정리한다.
2. XAML에서 레이아웃 구역을 나눈다.
3. 세션/채팅 UI를 먼저 안정화한다.
4. 파일/화면 상태 패널을 붙인다.
5. 서비스 결과를 로그/상태 문자열로 연결한다.

## 4. 인원 부족 시 축소 운영

### 4명일 때

- 1번: 공통 코어
- 2번: 세션 / 네트워크
- 3번: 화면 송신
- 4번: 파일 전송 + 클라이언트 UI

### 3명일 때

- 1명: 공통 코어
- 1명: 세션 / 네트워크 + 화면 송신
- 1명: 파일 전송 + 클라이언트 UI

### 2명 이하일 때 우선순위

1. 코어 계약
2. 세션 흐름
3. 채팅
4. 파일 전송
5. 화면 송신

## 5. 기능별 권장 구현 순서

### 세션
1. open / close
2. join / leave
3. disconnect / heartbeat
4. 참가자 수 동기화
5. 서버/클라이언트 UI 반영

### 채팅
1. `ChatPacket` 규칙 확인
2. 서버 수신/브로드캐스트
3. 클라이언트 수신
4. UI 반영
5. 빈 메시지/미참가 상태 검증

### 파일 전송
1. `FilePacket` 메타데이터 규칙 확인
2. 청크 생성
3. 청크 수신/조립
4. 체크섬 검증
5. 저장 / 실패 처리 / UI 반영

### 화면 송신
1. `ScreenPacket` 메타데이터 규칙 확인
2. 실제 프레임 생성
3. 반복 송신 루프
4. 수신 상태 반영
5. 실패 fallback

## 6. 충돌을 줄이는 방법

- 같은 파일을 여러 명이 동시에 오래 잡지 않는다.
- 코어 구조 변경과 기능 구현은 가능한 한 분리한다.
- UI 변경과 서비스 구조 변경을 동시에 크게 밀지 않는다.
- 역할이 다르면 브랜치와 PR도 다르게 쪼갠다.
- 문서 PR은 코드 PR과 분리한다.

## 7. 최종 목표

각 담당은 자기 역할 안에서 다음 상태를 만드는 것을 목표로 합니다.

- 공통 코어: 계약과 규칙이 흔들리지 않는 상태
- 세션/네트워크: 참가/이탈과 상태 동기화가 되는 상태
- 화면 송신: 실제 프레임 생성과 반복 송신이 되는 상태
- 파일 전송: 청크/검증/저장 흐름이 되는 상태
- 클라이언트 UI: 사용자가 상태를 이해할 수 있는 화면이 되는 상태
