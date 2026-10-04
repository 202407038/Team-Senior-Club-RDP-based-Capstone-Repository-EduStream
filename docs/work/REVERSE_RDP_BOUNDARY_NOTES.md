# 역방향 RDP(학생 → 교수자) 3번 구현 범위와 타 담당 협의 요청

작성 주체: 3번 [화면 송신 / RDP]. 이 문서는 3번이 **실제로 구현한 것**, **일부러 건드리지 않은 것**, **다른 담당이 해야 연결되는 것**을 구분해 기록합니다. 다른 담당 코드는 수정하지 않았고, 필요한 필드·함수·책임 경계만 아래에 적습니다. 이 문서의 어떤 항목도 "전체 완료"를 뜻하지 않습니다. 검증 결과는 [REVERSE_RDP_VERIFICATION_REPORT.md](./REVERSE_RDP_VERIFICATION_REPORT.md)에 따로 둡니다.

## 1. 3번이 수정/추가한 파일 (3번 영역: `Server/Rdp`, 테스트)

| 파일 | 내용 |
| --- | --- |
| `src/EduStream.Server/Rdp/ReverseSessionManager.cs` | `ConnectionString` 키 매핑 테이블, 실제 `OnAttendeeConnected` 이벤트에서 ProfessorId/StudentId/만료/세대/중복 검증 후 승인·거부, 호스트 허용(`GrantControlAsync`)·회수(`RevokeControlAsync`), 실패/종료/이탈 시 매핑 정리 |
| `src/EduStream.Server/Rdp/ReverseInvitationWire.cs` (신규) | 역방향 초대 전용 계약 DTO. 필수 필드 유실 시 역직렬화 즉시 실패 |
| `src/EduStream.Server/Rdp/IReverseSessionManager.cs` | 기존 인터페이스 유지, `ReverseControlMode`와 `InvitationId`만 추가 |
| `src/EduStream.Server/Rdp/WdsViewportAdapter.cs` | `ApplyViewportSettings`가 실제 컨트롤에 `SmartSizing`/`Bounds`를 적용하고 읽어서 확인한 뒤에만 `ViewerApplied` 발생 |
| `src/EduStream.Server/Rdp/AnnotationEngineAdapter.cs`, `IAnnotationEngineAdapter.cs`, `AnnotationManager.cs` | 숨김/재표시/전체 삭제/실행 취소를 렌더·전송 싱크에 스냅샷으로 전달(`AddLayerSyncHandler`, `OnLayerSynced`). 숨긴 동안의 스트로크는 보존만 하고 출력하지 않음 |
| `tests/EduStream.FileTransfer.Tests/AdapterDefectRegressionTests.cs`, `AnnotationLayerSyncTests.cs`, `*.csproj` | 단위 테스트와 `EDUSTREAM_WDS_SMOKE=1` 전용 실제 WDS E2E |

## 2. 일부러 수정하지 않은 것과 이유

- `src/EduStream.Core/Models/RdpInvitationPacket.cs`, `src/EduStream.Core/Utils/RdpInvitationContract.cs` (1번 Core)
  - 정방향(교수자 → 학생) 초대 계약은 `ViewOnly=true`만 허용합니다. 이 계약은 그대로 둡니다.
  - 역방향은 `ViewOnly=false` + 호스트가 허용해야 올라가는 제어 모드가 필요하므로, 정방향 패킷에 값을 끼워 맞추지 않고 별도 계약(`ReverseInvitationWire`)을 3번 영역에 두었습니다.
  - 정방향 계약이 여전히 `ViewOnly=false`를 거부한다는 것은 단위 테스트(`ForwardContract_StillRejectsNonViewOnlyPackets_SoReverseUsesItsOwnContract`)로 고정했습니다.
- `src/EduStream.Server/Services/RdpSharingService.cs`, `src/EduStream.Client/Services/RdpViewerService.cs`, `src/EduStream.Client/ViewModels/ClientViewModel.cs`, `IRemoteInputGate` (2번·5번 영역)

## 3. `ReverseInvitationWire` 필드 계약 (전송 담당이 보존해야 하는 값)

| 필드 | 규칙 |
| --- | --- |
| `ContractVersion`, `Provider`(`windows-desktop-sharing`), `Direction`(`student-to-professor`) | 고정값. 다르면 거부 |
| `SessionId`, `SharingId`, `InvitationId`, `ConnectionId` | 모두 필수, 빈 GUID 금지 |
| `ProfessorId`, `StudentId`, `ParticipantId` | 모두 필수. `ParticipantId`는 `ProfessorId`와 같아야 함 |
| `ConnectionString` | 필수, UTF-8 64KB 이하, `DataLength`와 일치 |
| `ExpiresAt` | 만료 후에는 접속 이전 검증에서 거부 |
| `ControlMode` / `ViewOnly` | `HostGrantedInteractive`이면 `ViewOnly=false`. 서로 모순이면 거부 |
| 초대 **비밀번호** | JSON에 넣지 않음. 별도 경로로 전달(테스트가 JSON에 비밀번호가 없음을 확인) |

수신 측은 `ReverseInvitationWire.FromJson()` 후 반드시 `Validate(기대 세션, 기대 교수자, 기대 연결, 현재 시각, 기대 학생)`을 통과시킨 뒤 접속해야 합니다. 빈 값을 임의 ID로 채우거나 검증을 건너뛰는 방식은 쓰지 않습니다.

## 4. 타 담당에게 필요한 작업 (3번은 수정하지 않음)

### 1번 (Core / 계약)
- 역방향 초대를 전송 메시지로 실어 나를 계약 위치를 결정해 주세요.
  - 선택지 A: Core에 역방향 전용 패킷/검증을 두고 위 필드를 그대로 사용.
  - 선택지 B: `ReverseInvitationWire`를 Core로 이동(3번은 이동 후 참조만 변경).
- 정방향 `RdpInvitationPacket`에 `ProfessorId`/`StudentId`를 추가하거나 `ViewOnly`를 풀지 마세요. 정방향 보기 전용 보장이 깨집니다.

### 2번 (Server 서비스 / 정책)
- 학생 → 교수자 방향의 초대 전달 경로(메시지 종류, 전달 대상, 비밀번호 별도 전달)가 아직 없습니다. 현재 E2E는 JSON 왕복만 검증하며 실제 전송은 검증하지 않았습니다.
- `IRemoteInputGate` 연결:
  - 인터페이스는 2번 소유이고, 3번이 호출할 실제 구현이 필요합니다.
  - 3번 제안: 구현체를 `Server/Rdp`에 두고 `GrantAsync`는 `ReverseSessionManager.GrantControlAsync` 완료 후 `WindowsNativeInputPipeline.InjectInputAsync`, `RevokeAsync`는 `RevokeControlAsync` 후 `BlockInputAsync`로 묶는 방식. `RemoteControlState`의 대상 ID와 `ProfessorId` 매핑 규칙은 2번과 합의가 필요합니다.
  - **이 구현체는 아직 작성하지 않았습니다**(3번 미완료 항목, 아래 5번).
- 승인/회수 정책의 결과(승인, 회수, 학생 거부, 단절)를 `GrantControlAsync` / `RevokeControlAsync` / `StopReverseSharingAsync` 호출로 연결해 주세요. 3번 쪽은 호스트 허용 없이는 제어 레벨이 오르지 않도록 이미 막고 있습니다.

### 5번 (UI / 바인딩)
- 교수자 뷰어 서비스(`RdpViewerService`)와 `ClientViewModel`은 아직 정방향 계약만 처리합니다. 역방향 초대는 `ReverseInvitationWire.Validate` 후 뷰어 `Connect`로 연결하는 별도 경로가 필요합니다.
- 판서 툴바 바인딩:
  - 숨김/표시/지우기/실행 취소는 `IAnnotationEngineAdapter`의 `SetLayerVisibilityAsync` / `ToggleLayerVisibilityAsync` / `ClearAllStrokesAsync` / `UndoAsync`를 호출하면 됩니다.
  - 화면 쪽 출력(로컬 캔버스 등)은 `AddLayerSyncHandler`로 등록하고, 받은 스냅샷(`VisibleStrokes`)으로 **출력을 통째로 교체**해야 합니다. `Visibility` 속성만 바꾸면 안 됩니다.
  - 학생 쪽으로 전달되는 동기화 메시지는 2번 전송 계약이 필요합니다.
- 배율: 뷰어 크기 조절은 `WdsViewportAdapter.ApplyViewportSettings(Rectangle)`를 호출하세요. 컨트롤이 `SmartSizing`을 지원하지 않으면 예외가 발생하고 `ViewerApplied`도 발생하지 않습니다.

## 5. 3번 미완료 / 알려진 문제

| 항목 | 상태 |
| --- | --- |
| `IRemoteInputGate` 실제 구현체 | 미작성. 현재 E2E는 같은 계약을 이벤트 구독으로 재현해 입력 엔진을 열고 닫음 |
| WDS 뷰어에서 발생한 마우스/키보드 입력 | 자동 검증 없음. 입력 검증은 OS 수준 `WindowsNativeInputPipeline` + 실제 커서 위치 |
| `OnAttendeeConnected` 내부 검증의 거부 경로 | 거부 시나리오는 모두 WDS 엔진 계층에서 먼저 거부됨(호스트 `Rejected` 이벤트 없음). 매니저 자체 검증(다른 교수자, 만료 직전 등)을 실제 엔진으로 직접 유발하지 못함 |
| 거부 경로 E2E 묶음 실행 | 스모크 모드에서 약 50% 확률로 테스트 호스트가 행/중단. 한 테스트 단독 필터 실행에서도 4회 중 1회 재현. 원인 미확인 |
| `WdsViewportAdapter.CalculateRenderBounds` | `ApplyFitMode` 뒤 줌을 한 번 더 적용하는 문제가 남아 있음(E2E는 이를 피해서 구성) |
| 다중 PC / 15분 병행 / 전체 시연 | 미수행. 모든 WDS 검증은 한 PC 안의 로컬 루프백 |
