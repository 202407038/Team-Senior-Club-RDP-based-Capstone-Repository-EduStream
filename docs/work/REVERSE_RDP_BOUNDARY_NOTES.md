# 역방향 RDP(학생 → 교수자) 3번 구현 범위와 타 담당 협의 요청

작성 주체: 3번 [화면 송신 / RDP]. 이 문서는 3번이 **실제로 구현한 것**, **일부러 건드리지 않은 것**, **다른 담당이 해야 연결되는 것**을 구분해 기록합니다. 다른 담당 코드는 수정하지 않았고, 필요한 필드·함수·책임 경계만 아래에 적습니다. 이 문서의 어떤 항목도 "전체 완료"를 뜻하지 않습니다. 검증 수치는 PR 본문에 적습니다.

## 1. 3번이 수정/추가한 파일 (3번 영역: `Server/Rdp`, 테스트)

| 파일 | 내용 |
| --- | --- |
| `src/EduStream.Server/Rdp/ReverseSessionManager.cs` | `ConnectionString` 키 매핑 테이블, 실제 `OnAttendeeConnected` 이벤트에서 ProfessorId/StudentId/만료/세대/중복 검증 후 승인·거부, 호스트 허용(`GrantControlAsync`)·회수(`RevokeControlAsync`), 실패/종료/이탈 시 매핑 정리 |
| `src/EduStream.Server/Rdp/ReverseInvitationWire.cs` (신규) | 역방향 초대 전용 계약 DTO. 필수 필드 유실 시 역직렬화 즉시 실패 |
| `src/EduStream.Server/Rdp/IReverseSessionManager.cs` | 기존 인터페이스 유지, `ReverseControlMode`와 `InvitationId`만 추가 |
| `src/EduStream.Server/Rdp/WdsViewportAdapter.cs` | `ApplyViewportSettings` 실적용 확인. `ApplyFitMode` 이후 `CurrentZoom`은 맞춤 대비 사용자 배율(1.0). `CalculateRenderBounds`는 맞춤 스케일을 한 번만 곱함 |
| `src/EduStream.Server/Rdp/ReverseWdsRemoteInputGate.cs` | 2번 `IRemoteInputGate` → 3번 `GrantControlAsync`/`RevokeControlAsync` 연결. OS 입력 파이프라인은 선택이며 뷰어발 WDS 입력과 구분 |
| `src/EduStream.Server/Rdp/AnnotationOverlayLayer.cs`, `AnnotationStrokeWire` | 제품용 판서 오버레이·전송 JSON. 5번은 창 배치, 1·2번은 JSON 전달 |
| `src/EduStream.Server/Rdp/AnnotationEngineController.cs`, `WdsSharedScreenPresentation` | Core `IAnnotationController` / `ISharedScreenPresentation` 구현. Pan은 WDS API 부재로 미지원 |
| `src/EduStream.Server/Rdp/AnnotationEngineAdapter.cs`, `IAnnotationEngineAdapter.cs`, `AnnotationManager.cs` | 숨김/재표시/전체 삭제/실행 취소를 렌더·전송 싱크에 스냅샷으로 전달 |
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
- 학생 → 교수자 방향의 초대 전달 경로가 아직 없습니다. E2E는 JSON 왕복만 검증합니다.
- `IRemoteInputGate` 구현체는 3번이 `ReverseWdsRemoteInputGate`로 제공했습니다. 2번은 서비스/UI를 고치지 말고 아래처럼 붙이면 됩니다.
  ```
  var gate = new ReverseWdsRemoteInputGate(
      reverseSession.GrantControlAsync,
      reverseSession.RevokeControlAsync,
      state => professorIdByConnection[state.Professor.ParticipantId],
      osInputPipeline); // 선택. OS 커서 경로. 뷰어발 WDS 입력과 다름.
  sessionManager.AttachRemoteInputGate(gate);
  coordinator.SetInputGate(gate);
  ```
- `RemoteControlState.Professor.ParticipantId`(Guid)와 역방향 초대 `ProfessorId`(문자열) 매핑 규칙은 2번이 정합니다.
- 판서 전송: `AnnotationStrokeWire.ToJson` / `FromJson` 페이로드를 메시지로 실어 나르면 됩니다. 전송 프로토콜은 1·2번.

### 5번 (UI / 바인딩)
- 역방향 초대: `ReverseInvitationWire.Validate` 후 뷰어 `Connect`. `RdpViewerService`/`ClientViewModel`은 이 PR에서 수정하지 않았습니다.
- 판서: `AnnotationEngineController`(`IAnnotationController`)와 `AnnotationOverlayLayer`를 붙이세요.
  - 로컬: `overlay.BindLocalRenderer(engine)`
  - 학생 창 배치만 5번, 그리기/숨김/삭제는 스냅샷 교체
  - 수신: `overlay.ReceiveRemoteStrokeJsonAsync(json)`
- 배율: `WdsSharedScreenPresentation`(`ISharedScreenPresentation`) 또는 `ApplyFitMode` → `CalculateRenderBounds` → `ApplyViewportSettings`. `PanAsync`는 WDS API가 없어 `NotSupportedException`입니다.

## 5. 3번 미완료 / 알려진 문제

| 항목 | 상태 |
| --- | --- |
| `IRemoteInputGate` → WDS Grant/Revoke | 제공됨 (`ReverseWdsRemoteInputGate`). 2번 서비스/UI 연결은 대기 |
| WDS 뷰어에서 발생한 마우스/키보드 입력 | **미검증.** 검증한 것은 (1) 게이트 → COM ControlLevel 3↔2, (2) 같은 PC `WindowsNativeInputPipeline` 커서. 두 경로는 다름 |
| `OnAttendeeConnected` 내부 검증의 거부 경로 | 거부 시나리오는 WDS 엔진 계층에서 먼저 거부됨. 매니저 자체 `Rejected` 분기는 실제 엔진으로 미유발 |
| 거부 경로 E2E 행 | 테스트 정리 순서. 거절된 `AxRDPViewer` 를 공유 세션이 살아있는 동안 Dispose/Disconnect 하면 WDS COM 이 멈춤. 거절은 이벤트로 확인한 뒤, 세션 `StopReverseSharingAsync` 이후에 뷰어를 Dispose/Close 한다. 객체 누수·창 숨김 없음. 2026-10-05 재검증: `Reverse_InvalidInvitation` 6회와 만료·침입·소진·재접속 포함 5건이 연속 통과했고 testhost 중단은 없었다. 제품 `Grant`/`Revoke` 경로의 행은 이 재현에서 확인되지 않음 |
| `CalculateRenderBounds` 중복 배율 | 수정함. 맞춤 → 휠 → 맞춤 복귀 회귀 테스트 추가 |
| 다중 PC / 15분 병행 / 전체 시연 | 미수행. 모든 WDS 검증은 한 PC 안의 로컬 루프백 |
