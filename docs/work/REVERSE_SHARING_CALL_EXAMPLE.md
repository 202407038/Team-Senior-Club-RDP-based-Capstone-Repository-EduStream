# 역방향 화면 공유 호출 예제

작성: 3번 [화면 송신 / RDP]. 학생 PC가 자기 데스크톱을 송신하고, 교수자 PC가 그 화면을 수신합니다. 학생 앱은 `EduStream.Server`(교수자 실행 파일)를 참조하지 않습니다. 2번의 전달 정책과 5번의 XAML은 이 문서가 대신 작성하지 않습니다.

## 1. 어느 프로세스가 무엇을 실행하는가

WDS 호스트는 자신이 실행 중인 PC의 데스크톱을 공유합니다. 교수자 프로세스에서 학생 ID마다 호스트를 열어도 그 학생 PC의 화면이 오지 않습니다.

| 실행 파일 | 프로젝트 | 참조 | 하는 일 |
| --- | --- | --- | --- |
| `EduStream 학생.exe` | `EduStream.Client` | Core + `EduStream.ShareHost` | 이 PC의 데스크톱 호스트를 엽니다. 초대 계약은 Core `ReverseRdpInvitationNotice`입니다. |
| `EduStream 교수자.exe` | `EduStream.Server` | Core + `EduStream.ShareHost` + `EduStream.ShareViewer` | 학생 PC가 만든 연결 문자열로 수신 뷰어만 붙입니다. 여기서 `RDPSession`을 열지 않습니다. |

`EduStream.ShareHost`는 Server 실행 파일을 참조하지 않습니다. `EduStream.ShareViewer`는 ShareHost와 Server를 참조하지 않습니다. 교수자 프로젝트가 ShareHost를 참조하는 이유는 `WdsViewportAdapter`, `ReverseScreenShareAdapter`, `ReverseWdsRemoteInputGate`가 ShareHost의 모니터·세션 타입을 쓰기 때문입니다. 이 참조로 교수자 프로세스에서 학생 ID마다 데스크톱 호스트를 열지는 않습니다.

## 2. 학생 PC

호출 위치는 학생 앱이 이미 참조하는 `EduStream.ShareHost.StudentDesktopHost`입니다.

```csharp
await using var host = new StudentDesktopHost(studentId);
var monitors = new MonitorDpiAdapter().GetMonitors();
var share = monitors.FirstOrDefault(m => m.DeviceName == selectedDeviceName)
            ?? monitors.First(m => m.IsPrimary);

var sharingId = await host.StartAsync(sessionId, share);
var password = invitationPassword; // 초대 계약에 넣지 않음
ReverseRdpInvitationNotice notice = await host.CreateInvitationAsync(
    sessionId, sharingId, professorId, connectionId, password, DateTimeOffset.UtcNow.AddMinutes(5));
// 2번이 notice를 Kind 15로, password를 Kind 16으로 해당 교수자 연결에만 전달합니다.

// 허용/회수는 이 학생 PC의 호스트에 적용합니다.
await host.GrantControlAsync(professorId);
await host.RevokeControlAsync(professorId);

await host.StopAsync();
```

모니터를 생략하면 이 PC의 데스크톱 전체를 공유합니다. 크기가 없는 모니터는 세션을 열기 전에 `ArgumentException`입니다. 같은 호스트의 두 번째 `StartAsync`는 `InvalidOperationException`입니다. 학생 둘은 교수자 PC에 호스트를 두 개 띄우는 방식이 아니라, 학생 PC 두 대가 각각 `StudentDesktopHost`를 실행하는 방식입니다.

## 3. 교수자 PC

5번이 UI 스레드에서 만든 `AxRDPViewer`를 `System.Windows.Forms.Control`로 넘깁니다. 수신 쪽은 공유 세션을 만들지 않습니다.

```csharp
using var reception = new ProfessorReception();
ProfessorViewerConnection connection = reception.Watch(
    studentId, viewerControl, notice.ConnectionString, notice.ProfessorId, password);

// 학생 PC의 호스트가 끝난 뒤에 이 뷰어를 해제합니다.
reception.Release(studentId);
```

다른 학생은 `Watch`를 한 번 더 호출합니다. 각 연결 문자열은 그 학생 PC의 호스트가 만든 값입니다. 같은 PC에서 호스트를 두 개 연 결과를 학생 두 대의 화면 수신으로 보지 않습니다.

## 4. 교수자 PC의 표시

맞춤과 배율은 수신 뷰어를 붙인 뒤에만 호출합니다. 뷰어 없이 `FitAsync`를 호출하면 `InvalidOperationException: WDS Viewer가 초기화되지 않았습니다.`가 납니다. 아래 순서는 교수자 UI 스레드에서 실행합니다. `FitAsync`, `ZoomAsync`, `Release`는 컨트롤 소유 스레드가 아니면 그 스레드로 옮겨 수행합니다.

좌표의 논리 단위(DIP)는 교수자 뷰어 모니터 배율로 픽셀로 바꿉니다. 학생 공유 모니터 배율로 바꾸지 않습니다. 그 픽셀에 원본 대비 뷰어 크기 비율을 곱한 뒤 학생 모니터의 `Left`/`Top`을 더합니다.

```csharp
reception.Watch(studentId, viewerControl, notice.ConnectionString, notice.ProfessorId, password);

var viewport = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
viewport.SetAxViewer(viewerControl);
viewport.SetSourceSize(new System.Drawing.Size(share.Width, share.Height));
var presentation = new WdsSharedScreenPresentation(viewport, () => new System.Drawing.Size(960, 540));
presentation.SetSharedMonitor(host.SharedMonitor);

await presentation.FitAsync();
await presentation.ZoomAsync(1.25, normalizedX: 0, normalizedY: 0);

var professorMonitor = new MonitorDpiAdapter().GetMonitors().First(m => m.IsPrimary);
System.Drawing.Point desktop = presentation.MapViewerPointToDesktop(
    new System.Drawing.Point(100, 50), viewerPointIsLogical: true, viewerMonitor: professorMonitor);

await host.StopAsync();
reception.Release(studentId);
```

`PanAsync`는 지원하지 않습니다. 예외가 나며 성공으로 처리하지 않습니다. 이 순서는 `tests/EduStream.FileTransfer.Tests/ReverseSharingCallExampleTests.cs`에서 실제 `AxRDPViewer`로 컴파일해 실행합니다. 한 PC에서 학생 호스트와 교수자 뷰어를 이어서 호출한 것이며, 학생 PC 두 대의 화면 수신이 아닙니다.
