# 역방향 화면 공유 호출 예제

작성: 3번 [화면 송신 / RDP]. 2번·5번이 기존 엔진을 붙일 때 보는 호출 순서입니다. 학생 앱(`EduStream.Client`)에 `EduStream.Server` 프로젝트 참조를 추가하는 방법은 쓰지 않습니다. 앱 수명·XAML·세션 정책 파일은 이 예제가 대신 수정하지 않습니다.

## 1. 어느 프로젝트가 무엇을 호출하는가

| 프로젝트 | 참조 | 하는 일 |
| --- | --- | --- |
| `EduStream.Client` (학생 앱) | `EduStream.Core`만 | Kind 15 `ReverseRdpInvitationNotice`, Kind 16 비밀, Kind 17 판서 봉투를 읽고 씁니다. `ReverseSessionManager`를 생성하지 않습니다. |
| `EduStream.Server` (이미 Server를 컴파일하는 실행 파일) | Core + 자신의 `Rdp` | 학생마다 호스트를 열고, 모니터 영역을 적용하고, 5번이 넘긴 뷰어 컨트롤의 수명을 끝냅니다. |

학생 앱이 초대 필드를 보려고 Server의 `ReverseInvitationWire`를 참조할 필요는 없습니다. 서버가 `ReverseInvitationWire.From(packet).ToContract()`로 Core 계약을 만들어 Kind 15로 넘깁니다. 비밀번호는 그 계약에 넣지 않고 Kind 16으로만 보냅니다.

## 2. 서버 프로세스에서의 호출

아래 코드는 `EduStream.Server` 안에서만 컴파일됩니다. 네임스페이스는 `EduStream.Server.Rdp`입니다.

```csharp
await using var room = new ReverseClassroomPlacement();
var station = room.StationFor(studentId); // 같은 studentId는 같은 호스트를 반환

var monitors = new MonitorDpiAdapter().GetMonitors();
var share = monitors.FirstOrDefault(m => m.DeviceName == selectedDeviceName)
            ?? monitors.First(m => m.IsPrimary);

var sharingId = await station.StartAsync(sessionId, share);
var password = invitationPassword; // Kind 16으로만 전달. 초대 계약에 넣지 않음
var packet = await station.Host.CreateProfessorInvitationAsync(
    sessionId, sharingId, professorId, connectionId, password, DateTimeOffset.UtcNow.AddMinutes(5));
var notice = ReverseInvitationWire.From(packet).ToContract();
// 2번: notice는 Kind 15, password는 Kind 16. 해당 교수자 연결에만 전달
```

모니터를 지정하지 않으면 기존처럼 데스크톱 전체를 공유합니다.

```csharp
var sharingId = await station.StartAsync(sessionId);
```

`share.Width` 또는 `Height`가 0 이하면 `StartAsync`는 세션을 열기 전에 `ArgumentException`을 냅니다. `SetDesktopSharedRect`가 실패하면 `InvalidOperationException`이며 그 세션은 열리지 않습니다. 같은 호스트에서 두 번째 `StartAsync`는 `InvalidOperationException`입니다. 학생 둘은 호스트를 하나 더 엽니다.

```csharp
var other = room.StationFor(otherStudentId);
var otherSharingId = await other.StartAsync(sessionId, otherShare);
await station.StopAsync(); // other의 세션과 뷰어는 유지
```

## 3. 교수자 뷰어

5번은 자신의 UI 스레드에서 `AxRDPViewer`를 만듭니다. Server 프로젝트는 그 COM 라이브러리를 참조하지 않습니다. 만들어진 컨트롤을 `System.Windows.Forms.Control`로 서버 프로세스의 UI 스레드에 넘깁니다.

```csharp
ProfessorViewerConnection connection = station.ConnectProfessor(
    viewerControl, notice.ConnectionString, notice.ProfessorId, password);
```

연결이 거절되거나 끊긴 컨트롤에 `Disconnect`를 다시 호출하지 않습니다. 해제 순서는 `StopAsync` 안에 있습니다. 공유 세션을 먼저 끝낸 다음, 살아 있는 뷰어만 끊고 컨트롤을 해제합니다. 거절된 뷰어를 세션이 살아있는 동안 `Dispose`하지 않습니다.

```csharp
await station.StopAsync();
// 또는 강의 종료 시 모든 학생
await room.StopAllAsync();
```

`StopAsync`가 끝난 같은 `station`으로 `StartAsync`를 다시 호출할 수 있습니다. 이전 초대의 `ConnectionString`은 새 세션에서 쓰지 않습니다.

## 4. 표시, 좌표, Pan

뷰어 컨트롤 크기를 원본에 맞출 때 맞춤 배율은 한 번만 적용합니다.

```csharp
var viewport = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
viewport.SetSourceSize(new System.Drawing.Size(share.Width, share.Height));
var presentation = new WdsSharedScreenPresentation(viewport, () => containerSize);
presentation.SetSharedMonitor(station.SharedMonitor);

await presentation.FitAsync();
await presentation.ZoomAsync(1.25, normalizedX: 0, normalizedY: 0);
```

`ZoomAsync`의 기준점 인자는 받지만, `AxRDPViewer`에 그 점을 기준으로 확대하는 API가 없어 배율만 적용됩니다.

뷰어 안 좌표를 공유 데스크톱 좌표로 바꿀 때는 컨테이너 여백을 뺀 컨트롤 내부 좌표를 넘깁니다. 논리 좌표(DIP)이면 `viewerPointIsLogical: true`입니다. 구현은 DPI로 물리 픽셀로 바꾼 뒤 공유 모니터의 `Left`/`Top`을 더합니다.

```csharp
System.Drawing.Point desktop = presentation.MapViewerPointToDesktop(viewerPoint, viewerPointIsLogical: true);
```

화면 이동은 지원하지 않습니다. `presentation.PanSupported`는 항상 `false`이고, `PanAsync`는 `NotSupportedException`과 `WdsSharedScreenPresentation.PanNotSupportedMessage`를 냅니다. 성공으로 처리하지 않습니다. 사용할 수 있는 조작은 `FitAsync`와 `ZoomAsync`입니다.

## 5. 입력 게이트

제어 허용·회수 정책은 2번입니다. 3번이 제공한 게이트를 서버 프로세스에서 붙입니다. 이 게이트의 OS 입력 경로는 뷰어가 보낸 마우스·키보드가 다른 PC에 도달했다는 증거가 아닙니다.

```csharp
var gate = new ReverseWdsRemoteInputGate(
    station.Host.GrantControlAsync,
    station.Host.RevokeControlAsync,
    state => professorIdByConnection[state.Professor.ParticipantId]);
sessionManager.AttachRemoteInputGate(gate);
```

## 6. 실행으로 확인하는 방법

같은 호출은 `tests/EduStream.FileTransfer.Tests/ReverseSharingPlacementTests.cs`에 있습니다. 실제 WDS가 필요하면 `EDUSTREAM_WDS_SMOKE=1`로 그 클래스를 실행합니다. 한 PC에서 확인된 것은 거절된 뷰어의 해제 순서, 같은 호스트의 재접속, 학생 둘의 독립 세션, 이 PC에 잡힌 모니터 1대의 공유 영역, DPI·원점 좌표입니다. 다른 PC의 뷰어 입력과 물리 모니터 2대의 공유 영역은 이 예제의 완료 조건이 아닙니다.
