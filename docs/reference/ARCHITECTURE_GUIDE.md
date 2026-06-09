# 아키텍처 가이드

## 1. 개요

EduStream은 교수자 화면을 학생들에게 전달하고, 파일과 텍스트 채팅을 함께 제공하는 WPF 기반 데스크톱 애플리케이션입니다.

현재 아키텍처는 다음 세 개의 프로젝트를 중심으로 구성됩니다.

- `EduStream.Server`
- `EduStream.Client`
- `EduStream.Core`

이 구조는 `BaseUML.mdj`와 `README.md`의 설계를 실제 코드 구조로 옮기기 위한 기준입니다.

## 2. 프로젝트별 책임

### EduStream.Core

공통 모델과 공통 유틸리티를 담당합니다.

포함 대상:

- 패킷 모델
- 직렬화기
- 공통 상태 모델
- 공통 로깅
- 공통 MVVM 유틸

넣지 말아야 할 것:

- 서버 전용 세션 호스팅 구현
- 클라이언트 전용 화면 렌더링 구현
- WPF 창 또는 페이지

### EduStream.Server

교수자 측 송신 기능과 세션 관리 기능을 담당합니다.

포함 대상:

- 세션 개설/종료
- 화면 캡처/송신
- 파일 배포
- RDP 호스트 연동
- 교수용 ViewModel과 UI

### EduStream.Client

학생 측 수신 기능과 상태 표현을 담당합니다.

포함 대상:

- 세션 참여/해제
- 화면 렌더링
- 파일 수신/검증/저장
- 학생용 ViewModel과 UI

## 3. 의존성 방향

참조 방향은 아래만 허용합니다.

```text
EduStream.Server -> EduStream.Core
EduStream.Client -> EduStream.Core
```

금지 규칙:

- `EduStream.Core -> EduStream.Server`
- `EduStream.Core -> EduStream.Client`
- `EduStream.Server -> EduStream.Client`
- `EduStream.Client -> EduStream.Server`

즉, 공통 코어는 가장 아래 계층이며, 서버와 클라이언트는 코어를 공유하지만 서로 직접 의존하지 않습니다.

## 4. 계층 분리 원칙

### UI

- XAML
- Window
- 사용자 입력과 화면 표시

UI는 보여주기와 이벤트 연결에 집중합니다.

### ViewModel

- 화면 상태 보관
- 사용자 액션을 커맨드로 제공
- 서비스 호출 결과를 UI 바인딩용 상태로 변환

ViewModel은 네트워크나 파일 로직을 직접 구현하지 않습니다.

### Service

- 실제 기능 수행
- 세션, 파일, 화면, 직렬화, 연결 상태 같은 핵심 기능 담당

서비스는 가능한 한 UI에 독립적이어야 합니다.

### Model

- 데이터 구조 정의
- 패킷/세션 정보/공통 상태 표현

## 5. 현재 코드 기준 패키지 설명

### 공통 유틸

- `EduStream.Core.Common.ObservableObject`
  - WPF 바인딩을 위한 속성 변경 알림 기반 클래스
- `EduStream.Core.Common.RelayCommand`
  - 커맨드 바인딩을 위한 최소 구현

### 로깅

- `EduStream.Core.Logging.ILogSink`
  - 로그 기록 인터페이스
- `EduStream.Core.Logging.InMemoryLogSink`
  - 메모리 기반 로그 저장 구현

### 공통 모델

- `BasePacket`
  - 패킷 공통 헤더
- `ChatPacket`
  - 채팅 메시지 데이터
- `FilePacket`
  - 파일 메타데이터와 본문
- `ScreenPacket`
  - 화면 프레임 데이터
- `SessionInfo`
  - 세션 연결 정보
- `PacketType`
  - 패킷 구분값

### 직렬화

- `PacketSerializer`
  - 현재는 JSON 기반
  - 향후 MessagePack 도입 시 교체 후보

### 서버 서비스

- `SessionManager`
  - 세션 개설/종료 및 브로드캐스트 진입점
- `ScreenCapturer`
  - 화면 캡처 스텁
- `RdpHost`
  - 향후 RDP 연동 위치
- `FileDistributor`
  - 파일 패킷 생성 및 체크섬 준비

### 클라이언트 서비스

- `SessionClient`
  - 세션 참여/연결 종료
- `ScreenRenderer`
  - 화면 표시 스텁
- `FileReceiver`
  - 체크섬 검증 및 저장

## 6. 설계 원칙

### 단일 책임 원칙

한 클래스는 한 가지 역할만 맡습니다.

예:

- 세션 관리와 파일 체크섬 계산을 같은 클래스에 넣지 않습니다.
- 화면 렌더링과 채팅 상태 관리를 같은 서비스에 넣지 않습니다.

### 교체 가능성 확보

현재 스텁 구현은 이후 실구현으로 쉽게 교체될 수 있어야 합니다.

예:

- `PacketSerializer`는 나중에 MessagePack으로 교체 가능해야 합니다.
- `RdpHost`는 MSTSCLib 또는 다른 래퍼 구현으로 대체 가능해야 합니다.

### 문서와 코드의 동기화

아래 항목 중 하나가 바뀌면 나머지도 같이 확인합니다.

- UML
- README
- 실제 클래스 구조

## 7. 현재 사용 기술과 도입 예정 기술

### 현재 사용 중

- C#
- .NET 8.0
- WPF
- 기본 BCL 기반 파일/직렬화/암호화 API

### 도입 예정 또는 검토 중

- `MessagePack-CSharp`
  - 패킷 직렬화 경량화
- `MSTSCLib` 또는 대체 RDP 연동 기술
  - 원격 세션 기능 검토
- 화면 캡처용 라이브러리 또는 Windows API
  - 실제 화면 스트리밍 구현

도입 예정 항목은 실제 패키지 추가 전까지 README와 문서에 "검토 중"으로 표시합니다.

## 8. 앞으로의 구현 우선순위

상세 일정과 우선순위는 [현재 상태와 로드맵](../work/STATUS_AND_ROADMAP.md)을 기준으로 관리합니다.

현재 아키텍처 관점의 우선순위는 아래와 같습니다.

1. 세션과 연결 상태 흐름 안정화
2. 파일 전송 UI 흐름과 실패 표시 정리
3. 화면 캡처/송신/렌더링 품질 보강
4. 다중 클라이언트 기준 예외 처리와 복구 로직 추가
5. README / docs / UML 동기화
