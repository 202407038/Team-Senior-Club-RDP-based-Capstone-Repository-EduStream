# 🎓 EduStream: RDP 기반 실시간 강의 공유 시스템

EduStream은 대학 강의 환경에서 교수님의 화면을 학생들에게 실시간으로 공유하고, 원활한 소통을 위한 파일 및 텍스트 전송 기능을 제공하는 C# 기반 데스크톱 애플리케이션입니다.

---

## 📌 목차
1. [주요 기능](#-주요-기능-key-features)
2. [사용자 워크플로우](#-사용자-워크플로우-user-workflow)
3. [시스템 아키텍처](#-시스템-아키텍처-system-architecture)
4. [기술 스택](#-기술-스택-tech-stack)
5. [성능 및 무결성 목표](#-성능-및-무결성-목표)
6. [구현 계획](#-구현-계획-roadmap)
7. [시작하기 (Getting Started)](#-시작하기-getting-started)
8. [협업 가이드라인](#-협업-가이드라인)
    - [브랜치 관리 및 머지 전략](#1-브랜치-관리-및-머지-전략)
    - [커밋 규칙](#2-커밋-규칙-commit-conventions)
    - [코드 가독성 및 개발 가이드라인](#3-코드-가독성-및-개발-가이드라인)
    - [API Handoff 및 프로토콜 명세](#4-api-handoff-및-프로토콜-명세)

---

## 📂 디렉터리 구조 (Directory Structure)
```text
Team-Senior-Club-RDP-based-Capstone-Repository/
├── EduStream.sln              # 통합 솔루션 파일
├── src/
│   ├── EduStream.Server/      # 교수용 Broadcaster 앱 (WPF)
│   ├── EduStream.Client/      # 학생용 Receiver 앱 (WPF)
│   └── EduStream.Core/        # 공통 통신 프로토콜 및 유틸리티 (Class Library)
├── docs/                      # 설계 문서 및 다이어그램
└── README.md
```
---

## ✨ 주요 기능 (Key Features)

* **실시간 화면 스트리밍**: RDP 기술 및 데스크톱 캡처 API를 응용하여 고해상도 화면을 다수의 학생 PC에 저지연으로 전송합니다.
* **파일 전송**: 강의 자료, 실습 코드 등을 서버(교수)에서 모든 클라이언트(학생)로 무결성 손실 없이 일괄 전송합니다.
* **실시간 텍스트 채팅**: 수업 중 질의응답을 위한 경량화된 메시지 전송 기능을 제공합니다.
* **네트워크 최적화**: 멀티캐스트(Multicast) 및 효율적인 데이터 직렬화를 통해 다중 접속 환경에서도 네트워크 부하를 최소화합니다.

---

## 🔄 사용자 워크플로우 (User Workflow)

1. **세션 개설 (Professor)**: 교수가 `EduStream.Server`를 실행하고 특정 포트로 강의 세션을 개설합니다.
2. **세션 접속 (Student)**: 학생이 `EduStream.Client`를 실행하여 교수의 IP 주소 및 포트(또는 접속 코드)를 입력하여 세션에 참여합니다.
3. **화면 및 자료 공유**:
   - 교수가 화면 공유 버튼을 클릭하면 실시간으로 학생들의 화면에 교수의 데스크톱 화면이 렌더링됩니다.
   - 수업 중 필요한 파일(예: `.zip`, `.pdf`)을 드래그 앤 드롭으로 전송하면 백그라운드에서 학생들에게 다운로드됩니다.
4. **상호작용**: 학생은 텍스트 채팅을 통해 실시간으로 질문을 남길 수 있습니다.
5. **세션 종료**: 교수가 세션을 닫으면 모든 클라이언트의 연결이 안전하게 종료됩니다.

---

## 🏗 시스템 아키텍처 (System Architecture)

*(여기에 시스템 구성도 이미지 삽입 예정)*

* **EduStream.Server (Broadcaster)**: 화면 캡처, RDP 세션 호스팅, 파일 분배 컨트롤러 역할을 수행합니다.
* **EduStream.Client (Receiver)**: 원격 세션 데이터 수신, 화면 렌더링, 파일 저장 역할을 수행합니다.
* **EduStream.Core (Shared)**: 서버와 클라이언트 간의 통신 규약, 소켓 연결 유지, 로깅 등을 담당하는 공통 라이브러리입니다.

---

## 🛠 기술 스택 (Tech Stack)

* **Language**: C# (.NET 8.0)
* **UI Framework**: WPF (Windows Presentation Foundation)
* **Networking**: TCP/IP (제어 신호, 텍스트, 파일 전송), UDP (화면 스트리밍)
* **Libraries**:
  * `MSTSCLib` / `FreeRDP` Wrapper (RDP 기능 구현 시)
  * `SharpDX` (고성능 화면 캡처)
  * `MessagePack-CSharp` (빠르고 가벼운 데이터 직렬화/역직렬화)

---

## 🎯 성능 및 무결성 목표

* **시각적 정보 무결성**: 텍스트나 코드가 뭉개지지 않도록 화면 캡처 및 압축 과정에서 가독성을 최우선으로 보장.
* **데이터 무결성 보장**: 대용량 파일 전송 시 체크섬(Checksum) 검증을 통해 패킷 유실 및 파일 손상을 방지.
* **Low Latency**: 로컬 네트워크(LAN) 환경 기준, 화면 전송 딜레이를 최소화하여 실시간성을 확보.

---

## 📅 구현 계획 (Roadmap)

* **Phase 1: 기반 아키텍처 및 UI 설계**
  - 솔루션 디렉터리 세팅 및 공통 Core 라이브러리 작성
  - WPF 기반 Server / Client 기본 UI 레이아웃 구현
* **Phase 2: 핵심 네트워크 및 통신 규약 구현**
  - TCP/UDP 소켓 통신 기반 마련 및 MessagePack 직렬화 테스트
  - 텍스트 채팅 기능 선행 구현을 통한 네트워크 안정성 검증
* **Phase 3: 화면 캡처 및 RDP 연동**
  - 데스크톱 화면 캡처 최적화 로직 개발
  - 클라이언트 렌더링 및 스트리밍 성능 튜닝
* **Phase 4: 대용량 파일 전송 및 부가 기능**
  - 비동기 스트림 기반의 파일 전송 로직 구현 (진척도 UI 반영)
  - 세션 예외 처리 및 끊김 복구 로직 추가
* **Phase 5: 리팩터링 및 최종 테스트**
  - 코드 컨벤션 점검 및 리팩터링
  - 다중 클라이언트(N > 20) 접속 부하 테스트 및 버그 픽스

---

## 🚀 시작하기 (Getting Started)

### 요구 사항 (Prerequisites)
* Windows 10 또는 11
* Visual Studio 2022 (또는 JetBrains Rider)
* .NET 8.0 SDK 이상

### 빌드 및 실행 방법
1. 저장소 클론: `git clone https://github.com/사용자명/EduStream.git`
2. Visual Studio에서 `EduStream.sln` 열기
3. 시작 프로젝트를 `EduStream.Server` 또는 다중 시작 프로젝트로 설정 후 `F5`로 실행

---

## 🤝 협업 가이드라인

### 1. 브랜치 관리 및 머지 전략
본 프로젝트는 **GitHub Flow**를 변형하여 사용합니다.

* `main`: 배포 및 시연이 가능한 안정적인 최신 버전 (Protected Branch)
* `feature/{기능명}`: 새로운 기능 개발을 위한 브랜치 (예: `feature/screen-capture`)
* `fix/{버그명}`: 버그 수정을 위한 브랜치 (예: `fix/socket-disconnect`)

**Merge 규칙**:
* 모든 작업은 `feature` 브랜치에서 진행한 후 `main`으로 Pull Request(PR)를 생성합니다.
* PR 리뷰(코드 리뷰 및 로컬 테스트 통과) 완료 후 `Squash and Merge`를 원칙으로 하여 커밋 히스토리를 깔끔하게 유지합니다.

### 2. 커밋 규칙 (Commit Conventions)
커밋 메시지는 다음 접두사를 사용하여 명확하게 작성합니다.

* `feat`: 새로운 기능 추가
* `fix`: 버그 수정
* `refactor`: 코드 리팩터링 (기능 변경 없음)
* `docs`: 문서 수정 (README, 주석 등)
* `style`: 코드 포맷팅, 세미콜론 누락 등 (코드 로직 변경 없음)
* `test`: 테스트 코드 추가 및 수정
* `chore`: 빌드 업무 수정, 패키지 매니저 설정 등

> **예시**: `feat: UDP 기반 멀티캐스트 화면 전송 기능 추가`

### 3. 코드 가독성 및 개발 가이드라인
지속적인 리팩터링과 유지보수를 위해 다음 원칙을 준수합니다.

* **명명 규칙 (Naming Conventions)**:
  - 클래스, 메서드, 프로퍼티: `PascalCase`
  - 로컬 변수, 매개변수: `camelCase`
  - private 필드: `_camelCase`
* **단일 책임 원칙 (SRP)**: View(WPF .xaml)의 코드 비하인드(.cs)에는 UI 조작 로직만 남기고, 비즈니스 로직과 네트워크 통신 로직은 반드시 분리된 Service나 ViewModel 계층에서 처리합니다.
* **비동기 프로그래밍**: UI 프리징(멈춤 현상) 방지를 위해 네트워크 I/O 및 무거운 작업은 반드시 `async/await` 패턴과 `Task`를 사용합니다. 비동기 메서드명은 `~Async` 접미사를 붙입니다. (예: `SendFileAsync`)
* **주석 작성**: 복잡한 비즈니스 로직이나 네트워크 패킷 구조 설계 부분에는 반드시 한국어로 `/// <summary>` XML 주석을 작성하여 의도를 명확히 합니다.

### 4. API Handoff 및 프로토콜 명세
서버와 클라이언트 간의 통신은 REST API가 아닌 **Custom Socket Protocol**을 사용합니다.
새로운 통신 기능 추가 시, `EduStream.Core` 프로젝트 내의 패킷 모델을 갱신하고 팀원과 명세를 공유합니다.

* **패킷 구조 정의**: 모든 데이터 전송은 `[헤더(메시지 타입, 데이터 길이)] + [페이로드(MessagePack 직렬화 데이터)]` 구조를 따릅니다.
* **Handoff 절차**:
  1. `Core.Models`에 새로운 데이터 클래스(`XxxPacket`) 추가.
  2. 서버 측 `PacketHandler` 로직 작성 및 수신 테스트.
  3. 클라이언트 측 UI 바인딩 및 송수신 연결.
  4. 관련 구조체 변경 시 PR 본문에 패킷 구조 변화 내용 명시.