# RDP 구현 기준 및 역할 간 연결 계약

계약 확정일: 2026-09-09. 구현/검증 갱신일: 2026-09-15, 코드 기준 main `ec994c6`.

2026-09-18 계획 안내: 방 단위 비밀번호·자동 재연결·영역/모니터 공유 개선은 [피드백 반영 계획](./SEPTEMBER_USABILITY_PLAN.md)에 정리했습니다. 아래 계약/현재 코드를 변경 완료한 것이 아닌 후속 구현 계획입니다. 변경 계약은 1번이 먼저 확정하고 담당별 구현·검증 뒤 이 문서를 갱신합니다. 이번 문서 검토·병합은 승인됐으며 후속 코드 게시·배포는 별도 지시를 따릅니다.

## 확정 구성

Windows Desktop Sharing API(RDPSRAPI)를 사용합니다.

- 교수자: RDPSession / IRDPSRAPISharingSession. 현재 로그인한 교수자의 데스크톱을 공유.
- 학생: RDPViewer / IRDPSRAPIViewer ActiveX를 Client WPF의 WindowsFormsHost에 배치.
- 기본 권한: 보기 전용 CTRL_LEVEL_VIEW(2). 학생 마우스/키보드 제어는 이번 필수 범위에 포함하지 않습니다.
- 화면 데이터: WDS가 제공하는 RDP 전송 경로 사용. PNG ScreenPacket은 보조 경로.
- 강의 제어·파일·채팅: 기존 TCP 경로 유지. RDP 제어에는 아래 신규 패킷 사용.
- 기존 교수자 RdpActiveXHost(MsRdpClient)는 원격 PC 접속 실험으로 남기며 공유 엔진으로 재사용하지 않습니다.
- IP·Windows 로그인 계정을 학생 접속 계약으로 사용하지 않습니다. RDPSession이 만든 불투명 초대 문자열을 그대로 RDPViewer.Connect에 전달합니다.
- 교수자 1개 RDPSession 안에 학생별 초대를 발급합니다. 각 초대 AttendeeLimit=1, 전체 학생 2명 이상 동시 공유는 실제 통합 관문에서 확인합니다.

이는 구현 기준 확정이며 제품의 실사용 지원 인증이 아닙니다. 다른 OS/PC는 아래 환경 점검과 실제 수신 테스트를 통과해야 합니다.

## 현 PC 확인 결과

Windows 레지스트리 DisplayVersion=25H2, CurrentBuild=26200. ProductName은 Windows 10 Home으로 표시되므로 표시 문자열만으로 OS 지원을 일반화하지 않습니다.

실행:
```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File scripts/Test-RdpSharingEnvironment.ps1
```

- 공유 CLSID 9B78F0E6-3E05-4A5B-B2E8-E743A8956B65 활성화 성공.
- 수신 CLSID 32BE5ED2-5C86-480F-A914-0FF8885A1B3F 활성화 성공.
- 공유 Open 성공, AttendeeLimit=2 초대 생성 및 ConnectionString 존재 확인.
- 초대 Revoked 설정과 공유 Close 성공.
- 이 PC의 등록 DLL은 rdpsharercom.dll / rdpviewerax.dll. ProgID 검색 실패만으로 미지원으로 판단하지 않습니다.
- 9월 15일 후속: 실제 viewer 두 개의 Connected 이벤트, 보기 전용 권한, 재접속/회수, 파일/채팅 병행을 현 PC에서 자동 검증했습니다. 다중 PC 화면 표시/방화벽 통과/지연/15분 실행은 미검증.
- 점검 스크립트는 임의 비밀번호를 만들고 공유를 즉시 종료하며 초대 문자열과 비밀번호를 출력하지 않습니다.

RDP 포트를 기존 강의 TCP 5000 또는 원격 데스크톱 3389로 고정 가정하지 않습니다. 3번이 생성한 초대와 실제 연결에서 주소/포트·방화벽 요구를 검증해 문서화합니다. OS 설정/방화벽을 자동으로 변경하지 않습니다.

## 공통 코드 계약

- IRdpSharingService: 3번이 구현. StartAsync, CreateInvitationAsync, RevokeInvitationAsync, StopAsync, DisposeAsync.
- IRdpViewerService: 5번이 구현. ConnectAsync, DisconnectAsync, StatusChanged, DisposeAsync.
- 서비스가 COM/STA/UI 스레드 처리를 내부에서 책임집니다. Core는 WPF/COM에 의존하지 않습니다.
- 연결 메서드 반환은 연결 성공이 아닙니다. 실제 OnConnectionEstablished 이벤트에서 RdpConnectionStatus.Connected를 발행합니다.
- 재접속마다 새로운 ConnectionId. 이전 연결 이벤트는 현재 상태에 적용하지 않습니다.
- ActiveX 표시 영역 생성/부착은 5번 Client 구현 내부의 추가 메서드로 처리하며 공유 Core에 UI 타입을 넣지 않습니다.

### 제어 패킷

기존 PacketType 1~8의 값은 그대로입니다. 신규 값 9~11은 확장 계약이며, 이번 서버/클라이언트가 함께 반영된 뒤 사용합니다. 1.0 헤더만으로 상대가 RDP 확장을 지원한다고 가정하지 않습니다.

| 타입 | 방향 | 필수 정보 | 처리 |
|---|---|---|---|
| RdpInvitationRequest=10 | 학생 → 교수자 | SessionId, SenderId, ParticipantId, ConnectionId, ContractVersion=1 | 2번이 실제 승인된 TCP 연결 신원과 대조 |
| RdpInvitation=9 | 교수자 → 해당 학생 1명 | 위 식별자 + SharingId, InvitationId, Provider, ConnectionString, ExpiresAt, ViewOnly | 학생은 현재 연결 시도와 일치할 때만 수락 |
| RdpInvitationRevoked=11 | 교수자 → 해당 학생 1명 | SessionId, ParticipantId, InvitationId, ConnectionId, Reason | 일치하는 연결을 종료하고 이전 연결 알림은 무시 |

Provider="windows-desktop-sharing", ContractVersion=1, ViewOnly=true.
DataLength는 초대 ConnectionString의 UTF-8 바이트 수, 요청/폐기 패킷은 0입니다.
초대 문자열 최대 64KiB는 이 프로젝트의 제어 패킷 제한이며 RDP 제품 자체 제한을 뜻하지 않습니다.

새 패킷을 기존 ACK/Chat/Screen에 숨겨 전송하지 않습니다. 2·5번은 명시적인 switch 분기와 서버 개별 전송 경로를 추가합니다. 공유 시작 전 요청은 RdpSharingNotStarted 오류로 거부하며, 구버전 클라이언트는 자동 RDP 접속하지 않습니다.

### 학생 한 명의 흐름

1. 5번: 강의 참가 성공 후 새 ConnectionId로 요청 생성. 현재 기대하는 세션/학생/연결 ID를 보관.
2. 2번: ValidateRequest(request, actualSessionId, authenticatedParticipantId) 호출. 신원은 패킷 주장값이 아니라 참가 승인된 연결에서 얻음.
3. 2번: 학생에게 이미 발급한 초대/연결이 있으면 정리 후 3번 CreateInvitationAsync 호출.
4. 3번: 같은 RDPSession에서 학생별 초대 생성. ConnectionString을 파싱/재작성하지 않고 반환. 초대 ID ↔ COM invitation ↔ 승인 참가자 매핑 유지.
5. 2번: 요청한 학생에게만 초대 전달. 브로드캐스트 금지.
6. 5번: Validate(invitation, expectedSession, expectedParticipant, expectedConnectionId, now) 후 초대를 보관. 사용자가 별도로 받은 비밀번호를 입력하고 RDP 연결을 누르면 만료/식별자를 다시 확인하고 ConnectAsync 호출.
7. 3번: OnAttendeeConnected에서 해당 invitation 매핑으로 허용 여부 확인 후 ControlLevel=2 적용. 표시 이름만으로 신원 판단 금지. 제어 권한 상승 요청 거부.
8. 5번: 실제 연결 성공/실패/해제 이벤트를 RdpConnectionStatus로 변환해 UI에 전달.
9. 2번: 이탈/세션 종료 때 3번 폐기 API 호출 및 필요 시 폐기 패킷 송신. 3번은 Revoked 설정뿐 아니라 활성 attendee도 해제.
10. 팀장: 2·3·5번 서비스/버튼을 조립하고 두 학생 및 파일/채팅을 실제 검증.

### 비밀번호와 만료

- 초대 비밀번호는 Windows 계정 비밀번호가 아닌 공유 전용 임의 값입니다. 공유 측에서 생성하며 학생에게 별도 경로로 전달하고 학생 UI에서 입력받습니다.
- 기존 암호화되지 않은 TCP에 초대 문자열과 비밀번호를 함께 싣지 않습니다. 이번 DTO에는 Password 필드가 없습니다.
- 3번의 invitationPassword 인수는 교수자 앱 내부 값입니다. 네트워크 전송용 객체나 로그에 포함하지 않습니다.
- 서버 기본 초대 수락 기한은 발급 후 5분으로 설정합니다. DTO의 ExpiresAt만으로 COM 초대가 자동 만료되지는 않으므로 서버가 미사용 초대를 기한 만료 시 폐기합니다.
- 이미 승인돼 연결된 학생은 5분 수락 기한 때문에 끊지 않습니다. 참가자 이탈·세션 종료·관리자 폐기 때 실제 연결을 해제합니다.
- 새 공유 시작은 새 SharingId, 재접속은 새 ConnectionId/InvitationId/초대 문자열. 예전 초대 재사용 금지.
- 초대 문자열도 민감 정보입니다. ToString은 가리지만 전체 JSON을 로그에 남기면 노출되므로 2·5번은 본문 로깅을 금지합니다.

## 독립 작업 경계 및 통합 순서

- 1번: 위 모델·검증·인터페이스를 제공. 팀원은 임의 필드/패킷 번호를 추가하지 않고 변경 요청.
- 2번: 서버 요청 승인·학생 개별 송신·수명 정리. 테스트에는 IRdpSharingService 대역 사용.
- 3번: 교수자 WDS 공유 서비스/초대/참가자 권한/폐기 구현. 학생 UI·SessionManager 직접 변경하지 않음.
- 5번: WDS viewer/표시/UI와 초대 수신·폐기 처리. 교수자 공유 엔진 직접 변경하지 않음.
- 팀장: 계약 → 2·3·5번 구현 PR → 실제 조립 → 4번 병행 검증 → 진행 문서 순서로 반영.

대역 검증을 실제 RDP 성공으로 기록하지 않습니다. 공유·수신 방식 선택은 이 문서로 확정했으므로 팀원이 다른 방식을 임의로 골라 구현하지 않습니다. 실제 연결 장애가 발견되면 원인과 재현 환경을 공유하고 계약 변경을 팀장이 결정합니다.

## 자동 검증 결과 (이전 9월 9일 기준선)

- 빌드 경고 0/오류 0, 신규 초대 계약 테스트 8/8 통과.
- 전체 최종 재실행 142/142 통과.
- 앞선 전체 실행에서 기존 ScreenShareHeartbeatConcurrencyTests가 대기하여 중단됐습니다. 30초 제한 재현 실행도 같은 테스트 대기를 보고했습니다. 해당 테스트 단독 실행은 main/보완 브랜치 모두 통과했고 이후 전체 재실행은 통과했습니다. 원인 미확정의 간헐 현상으로 통합 QA에서 추적합니다.
- 위 기록은 계약 확정 당시 결과입니다. 9월 15일 현 PC 실제 viewer 연결/두 학생 자동 통합은 완료했으며, 영상 픽셀의 수동 확인·다중 PC 지연·15분 실사용은 여전히 별도 검증 대상입니다.

## 9월 15일 구현 대응

- #36 SessionManager: 학생별 초대/발급 서비스 추적, 종료/교체 경합 회수, 비밀번호 별도 인계.
- #37 RdpSharingService: 전용 STA/메시지 루프, CLSID 활성화, 초대별 고유 AuthString/GroupName, native invitation 기준 참가 승인과 보기 전용.
- #38 RdpViewerService/ClientViewModel: UI STA에 실제 컨트롤 부착, Connected 이벤트 확인, 이전 연결 이벤트 차단, 사용자 비밀번호 입력.
- #39 ServerViewModel: WDS 공유 시작 후 세션에 부착, 중지/창 종료 때 회수/종료. 교수자 화면에는 비밀번호·ConnectionString을 표시하지 않으며 선택 학생 비밀번호만 복사해 별도 전달.
- #40 TCP 송신 대기 중 종료 시 잠금 폐기 경합 보완, #41 파일 진행→완료 및 재시도 알림 보정.
- 현재 일반 전체 186 통과 / 실제 WDS 선택 실행 3항목 건너뜀. 실제 WDS 포함 선택 검증 11/11 통과. 상세 한계와 수동 인수는 [통합 검증](./SEPTEMBER_RDP_INTEGRATION.md) 참조.
- 복사된 비밀번호도 민감 정보입니다. 공유 화면·공용 채팅에 붙여 넣거나 클립보드 기록/동기화에 보관하지 않도록 주의합니다. TCP 자체의 암호화/인터넷 배포 보안을 이번 구현 완료로 주장하지 않습니다.

## 공식 근거

- [Windows Desktop Sharing 개요](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/rdp/about-windows-desktop-sharing)
- [초대 및 참가 제한](https://learn.microsoft.com/en-us/windows/win32/api/rdpencomapi/nn-rdpencomapi-irdpsrapiinvitation)
- [Viewer.Connect](https://learn.microsoft.com/en-us/windows/win32/api/rdpencomapi/nf-rdpencomapi-irdpsrapiviewer-connect)
- [보기/제어 권한](https://learn.microsoft.com/en-us/windows/win32/api/rdpencomapi/ne-rdpencomapi-ctrl_level)
