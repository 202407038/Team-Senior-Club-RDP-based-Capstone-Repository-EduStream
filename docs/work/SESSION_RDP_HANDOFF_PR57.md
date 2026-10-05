# #63까지의 세션 기반과 3번 RDP 구현 인계

## 2026-10-05 현재 기준

main `5f26414`에는 #65·#67·#76·#51이 병합됐습니다. #77(1번 공통 역방향/판서 계약·취소 프레임 보완) → #78(4번 실제 파일 회귀)은 게시·검증 완료 후 병합 대기입니다. 아래 이전 날짜 기록보다 [#51 이후 공통 기반·담당별 연결 순서](./POST_PR51_CORE_FILE_HANDOFF.md)를 우선합니다.

엔진 코드 제공과 서비스/최종 UI 연결을 구분합니다. 2번 인증 라우팅·권한/회수, 3번 실제 엔진 소비·입력/수명 관리, 5번 UI 부착·바인딩, 1·4번 공통/파일 통합 검수 책임은 유지합니다. 10/5~11 소비·통합, 10/12~18 QA, 10/19~25 내부 검수·11/1 외부 목표를 유지하며 이번 ZIP 배포는 없습니다.

아래는 작성 날짜 기준 이력입니다. '#51 미병합', '기존 UI 그대로', '4번 취소 보완은 이번 범위 제외'를 현재 상태나 현재 PR 범위로 해석하지 않습니다.

갱신: 2026-09-29. 코드 기준 main `ea87cbf` (#52~#59 및 #61~#63 병합). 기존 링크 호환을 위해 파일명은 유지합니다.
문서의 PR 번호는 기능 식별용이며 머지 순서가 아닙니다. 이 문서는 역할 변경이나 완료 기준 완화가 아니라 **이미 제공된 기능과 남은 구현을 구분하는 최신 인계**입니다.

## 2026-10-04 UI 수정 요청용 참가 API 인계

코드 기준 main `b0ffe05` (#73·#74). 아래 9/29 기록보다 이 절의 LAN 참가 정책이 우선합니다. #68~#71은 #72로 철회됐으며 재적용 대상이 아닙니다.

### 제공된 기반과 호출

- 1번: `LanSessionEndpoint.Create(host, sessionPort = 5000)`. IP와 세션 포트를 검사하고 보호 채널은 기존 `CollaborationPorts.ForSession(port)` 규칙(+1)을 유지합니다.
- 2번: `SecureRoomJoinClient.AuthenticateAsync(host, displayName, password, logSink, ...)`는 코드 없이 기본 5000으로 인증합니다. 고급 설정용 `AuthenticateAsync(host, sessionPort, displayName, password, logSink, ...)`도 제공합니다.
- 인증 후 반환하는 SecureSessionChannel의 참가 티켓을 기존 TCP 참가에 사용합니다. 기존 승인 ACK·채널 수명·재연결 토큰·권한 검사·초대 자동 전달 흐름을 유지합니다. 인증 성공만으로 참가 승인 또는 실제 WDS 표시 완료로 처리하지 않습니다.
- 실패는 `InvalidAddress`, `InvalidCertificate`, `PasswordRejected`, `LockedOut`, `VersionMismatch`, `Unreachable`, `ReconnectRejected`로 구분합니다. 자동 재연결은 같은 API에 기존 reconnectToken을 넘기며 방 비밀번호 재입력을 요구하지 않습니다.
- 구 connectionCode 인자형 메서드와 InvalidCode/CodeMismatch 별칭은 기존 UI 빌드 호환용입니다. 새 서비스는 코드 인자를 검사하지 않습니다. 구 지문 검증은 주석으로 보존했고 새 UI에서는 이 호환 경로를 사용하지 않습니다.
- TLS는 유지하되 서버 지문 대조는 제거했습니다. 유효기간/서버 용도 검사는 교수자 신원 인증이 아니며 신뢰 LAN 전용입니다.

### 5번이 기존 PR에서 마무리할 부분

1. 양 앱 XAML·화면 코드·ViewModel은 이번 선행 변경에서 수정하지 않았습니다. ClientViewModel의 구 코드 입력 검사, JoinTarget의 Code, 참가/재연결 호출, 구 오류 안내 및 입력 UI를 새 API 기준으로 바꿉니다.
2. 학생 이름·교수자 IP·선택적 방 비밀번호만 기본 참가 화면에 두고, 포트는 LanSessionEndpoint.DefaultPort를 사용합니다. 교수자 IP는 기존 HostNetworkInfoService.GetAddresses() 결과를 선택/복사하도록 연결합니다.
3. 세션 이름/수동 RDP/개발자 테스트 UI 제거, 교수자 방 비밀번호·입력 잠금 안내, 양쪽 채팅/접힌 로그, 목록 상태 유지, 파일 드롭/파일명 다운로드는 [U01~U09](./UI_FEEDBACK_SPEC.md)로 검증합니다.
4. #67에는 이전 #65 서버 커밋과 #68이 포함돼 있으므로 최신 main 병합 충돌을 검토합니다. 철회된 #70 UI나 #69 파일 취소 수정을 다시 가져오지 말고 5번의 기존 PR 수정으로 연결합니다. 새 PR/작성자 변경/force push 없이 기존 PR을 이어갑니다.
5. 서비스 기반 부족으로 실제 불가능한 항목만 미완료로 분리합니다. 필요한 API·담당·구현 여부·막힌 이유·가능한 UI 준비·완료 조건을 적습니다. 3번 엔진을 UI 담당자가 중복 작성하라는 요청이 아닙니다.

### 검증과 한계

- #73 주소 계약 14건, #74 관련 계약/실제 루프백 TLS·방 인증·재연결·권한/초대 97건 통과. Debug/Release 전체 각각 511 통과·실제 WDS 선택 테스트 3건 건너뜀·실패 0.
- #73 첫 전체 검증에서는 기존 파일 취소 테스트가 1회 실패했고 재실행은 통과했습니다. #69를 철회했으므로 이 별도 간헐 결함은 미수정이며 이번 참가 변경의 해결 항목이 아닙니다.
- 실제 UI 조작·다중 PC·WDS 표시/입력/판서 인수와 사용자 배포는 별도입니다. 기존 ZIP preview.2를 유지하며 이번 선행 수정으로 ZIP을 만들지 않습니다.

## 1. 현재 기준과 읽는 순서

1. [현재 진척](./STATUS_AND_ROADMAP.md)의 9/29 기록과 이 문서를 먼저 읽습니다.
2. [역할 배분](./UI_RDP_WORK_ALLOCATION.md)의 2번/3번/5번 경계를 따릅니다.
3. [확정 요구](./UI_FEEDBACK_SPEC.md) 및 [전체 주차별 로드맵](./FULL_WEEKLY_ROADMAP.md)의 9월 4~5주차를 대조합니다.
4. [기존 단방향 RDP 계약](./RDP_IMPLEMENTATION_CONTRACT.md)은 기존 교수자→학생 공유의 구현 참고입니다. 과거의 '학생 수신은 5번' 설명을 신규 학생→교수자 엔진까지 5번에게 배정한 것으로 해석하지 않습니다.
5. [1·4번 인계](./FINAL_CORE_FILE_HANDOFF.md)의 계약·파일 정책은 유지하며, 아래에 명시된 #61까지의 구현을 더 이상 전체 미구현으로 취급하지 않습니다. #62·#63 후속 회귀는 [별도 기록](./CORE_FILE_INTEGRATION_READINESS.md)으로 구분합니다.

'인터페이스 있음', '테스트 대역에서 성공', '실제 엔진 동작', '앱 연결', '최종 다중 PC 인수'는 서로 다른 상태입니다. 각각의 근거를 따로 적습니다.

## 2. main에 반영된 2번 작업과 한계

| PR | 제공된 코드/기능 | 아직 완료로 볼 수 없는 부분 |
|---|---|---|
| [#52](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/52) | ParticipantRegistry, ServerRemoteControlCoordinator, IRemoteInputGate, 파일 요청 인가. 실제 참가 연결 대조 및 단일 제어 대상 승인/회수 | 학생 OS 입력 엔진 자체. IRemoteInputGate는 엔진을 연결할 계약이지 마우스·키보드 구현체가 아님 |
| [#53](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/53) | RoomPasswordVerifier 해시 검증·시도 제한, HostNetworkInfoService | 보호 앱 참가 인증은 후속 #58에서 연결. 다중 PC 신뢰/실행 UX 인수는 별도 |
| [#54](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/54) | 공유 중지 시 제어 회수·차단 확인 결과, SessionManager의 파일 카탈로그 연결 | 실제 native 차단은 3번 엔진 필요. 카탈로그 API만으로 앱 다운로드 전체가 완성되지는 않음 |
| [#55](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/55) | 학생 먼저 실행 시 교수자 실행 제한, 교수자 먼저 실행 시 학생 실행 허용 | 같은 Windows 로그인 세션의 실행 정책이며 다른 PC 전체를 통제하는 기능 아님 |
| [#56](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/56) | SessionFileTransferRouter/SessionFileRequestClient, 요청별 전송·취소·저장 ACK 대조. 빠른 저장 ACK와 최종 송신 경합 보완 포함 | 보호 채널/양 앱 기본 파일 연결은 후속 #59 반영. 최종 UI/배포 인수는 별도 |
| [#57](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/57) | 공유 중지/재시작 시 초대 재요청/재발급 및 경합 보완 | 자동 비밀 전달/Connect는 #61 반영. 실제 화면 자동 복귀 인수와 제어권 재승인은 별도 |
| [#58](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/58) | TLS 보호 방 인증·일회용 TCP 참가 티켓·권한/제어 상태·재연결 토큰·종료 알림, 승인 ACK 후 참가 확정 | 실제 입력 엔진/역방향 초대 계약은 별도. 다중 PC 실환경 인수 필요 |
| [#59](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/59) | 참가한 보호 연결의 파일 라우팅, 양 앱 등록/목록/해제/다운로드와 실제 루프백 TLS/TCP 검증 | 드롭·최종 UI 배치 및 실제 Downloads 권한/RDP 병행/배포 검수 별도 |
| [#61](https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream/pull/61) | RdpInvitationSecretNotice 보호 전달, 초대/비밀 순서 무관 결합·자동 Connect, 중복 연결 차단 | 자동 테스트의 viewer는 대역. 실제 WDS 화면 복귀·신규 역방향 화면/입력 엔진 완료가 아님 |

위 서비스·정책의 코드 병합은 완료됐지만 **2번 전체 작업 또는 U03/U07/U08 전체 인수가 완료됐다는 뜻은 아닙니다.**

검토 중인 브랜치는 main의 확정 기반과 구분합니다(9/29 확인 시점).

- #51: 3번 신규 어댑터 검토본이며 main에 포함하지 않습니다. 상태/이벤트 보완과 실제 native 엔진 완성을 구분하고 후속 head는 별도로 재검토합니다.
- #58/#59/#61은 더 이상 미병합 대기가 아닙니다. 단, main 코드 반영과 Releases 실행 파일 배포는 다릅니다.

## 3. 기존 #51 브랜치를 최신화하는 순서

새 PR로 갈아타거나 기존 작성자를 바꾸지 않습니다. **기존 #51 브랜치에 최신 main을 병합하고 같은 브랜치에 후속 커밋을 추가**합니다.

1. 실제 EduStream 새 저장소 작업 폴더인지 확인합니다. 이전 원본 저장소와 혼동하지 않습니다.
2. `git remote -v`, `git status --short --branch`로 원격 URL·브랜치·미커밋 변경을 확인합니다.
3. 원격 URL은 `https://github.com/202407038/Team-Senior-Club-RDP-based-Capstone-Repository-EduStream.git`이어야 합니다. 팀장 환경은 `new-origin`, 팀원 환경은 `origin`일 수 있습니다.
4. 미커밋 변경이 있으면 먼저 내용을 보존·정리합니다. 강제 초기화/강제 덮어쓰기/무조건 stash 삭제를 하지 않습니다.
5. 해당 원격을 fetch하고, #51 브랜치를 원격의 같은 브랜치와 fast-forward로 동기화합니다.
6. 해당 원격의 최신 main을 현재 작업 브랜치에 merge합니다. 충돌은 변경 의도를 비교해 해결하며 어느 한쪽 전체를 무조건 선택하지 않습니다.
7. 문서와 코드를 함께 읽은 뒤 빌드/테스트, 본인 기능 구현과 회귀를 진행합니다. 구현과 테스트 커밋은 분리하고 동일 브랜치에 일반 push합니다. force push는 전제하지 않습니다.

원격 이름이 `origin`이고 위 URL 확인 및 미커밋 변경 정리를 마친 경우의 예시입니다. 팀장 환경에서는 원격 이름만 `new-origin`으로 바꿉니다.

```powershell
git fetch origin --prune
git switch feature/september-week4-rdp-adapters
git merge --ff-only origin/feature/september-week4-rdp-adapters
git merge origin/main
dotnet build EduStream.sln
dotnet test EduStream.sln --no-build
```

fast-forward가 불가능하거나 충돌이 발생하면 일단 이력을 확인합니다. 문서 갱신 PR을 포함한 최신 main을 기준으로 해야 하며, 위 코드 기준 SHA에 작업을 고정하지 않습니다. main을 합치는 것만으로 누락된 엔진이 생성되지는 않습니다.

## 4. 3번이 바로 연결할 수 있는 main의 접점

경로는 저장소 루트 기준이며, 아래 API는 #61까지의 main에 실제 존재합니다.

| 파일/접점 | 2번이 이미 제공한 것 | 3번의 다음 작업 |
|---|---|---|
| `src/EduStream.Server/Services/ParticipantRegistry.cs` | 승인된 참가자/연결 ID, 목록·권한 revision, 연결 제거/권한 변경 | 입력/화면 대상은 실제 연결 식별자와 연결. 학생 표시 이름이나 패킷 주장 ID만으로 신뢰하지 않음 |
| `src/EduStream.Server/Services/IRemoteInputGate.cs` | `GrantAsync(RemoteControlState, CancellationToken)`, `RevokeAsync(...)` 계약 | 실제 대상 PC 입력 허용/차단을 수행하는 구현 제공. 실패는 예외/취소로 알리고 완료 전에 성공하지 않음 |
| `ServerRemoteControlCoordinator.cs` | 기존 대상 회수 확인 후 새 대상 승인, 이전 요청 취소, 차단 대기 추적 | 승인 정책을 다시 작성하지 않고 native 성공/실패 결과를 돌려줌 |
| `SessionManager.AttachRemoteInputGate` | 실제 입력 게이트 등록 | 초기화 시 본인 구현 연결. 제어 진행 중 임의 교체하지 않음 |
| `SessionManager.RequestControlAsync` / `StopControlAsync` | 대상 선택 및 종료 진입점 | 승인/회수 결과가 실제 학생 입력 결과와 일치하는지 검증. Request 호출 반환만으로 Active를 단정하지 않음 |
| `SessionManager.UpdateParticipantPermissionsAsync` | 서버 내부 권한 변경 및 차단 확인 | 허용 철회/이탈/단절 때 실제 입력 중단 검증. 학생 앱의 보호된 변경 메시지는 #58 연결 범위와 구분 |
| `SessionManager.AttachRdpSharing` / `DetachRdpSharingAsync` | 공유 시작 연결 및 중지/초대 정리/입력 회수 흐름 | 실제 공유 서비스와 연결하고 Pending/Failed를 차단 성공으로 표시하지 않음 |
| `SessionManager.IsControlInputRevokePending` / `ServerRemoteControlCoordinator.ConfirmInputRevokedAsync` | 회수 미확인 상태 조회 및 조정자 수준 재확인 | 엔진 차단 실패 복구 후 기존 회수 흐름으로 재확인. SessionManager의 `ConfirmControlInputRevokedAsync`는 내부 private 메서드이므로 UI가 직접 호출하는 API로 취급하지 않음 |
| `TryGetPendingInvitationHandoff` / `RdpInvitationPasswordReady` 및 `RdpInvitationSecretNotice` | 기존 수동 인계 접점과 #61 단방향 초대 비밀 보호 전달 | 기존 공급자 구현 참고. 역방향 초대는 별도로 인증 대상·수명·방향을 1·2번과 조율 |

`INativeInputPipeline`은 #51 브랜치에서 정의한 접점입니다. **#52가 그 구현체를 제공하기를 기다리는 관계가 아닙니다.** #52의 `IRemoteInputGate`가 상위 승인/회수 계약이며, #51이 선택한 하위 입력 엔진을 실제로 구현·연결해야 합니다.

## 5. 3번이 구현하고 증빙할 부분

아래는 기존 역할/피드백 요구를 구체화한 것이며, 2번의 인증 구현이나 5번 전체 UI까지 대신 작성하라는 요청이 아닙니다. 특정 SendInput/드라이버 후킹 방식 자체를 강제하지 않습니다. 기존 WDS/RDP 선택과 양방향 권한 요구를 만족하는 기술을 사용하고, 공급자/전송 구조 변경이 필요하면 먼저 협의합니다.

### 5.1 학생 화면 → 교수자 실제 표시

- 학생 측 실제 공유 시작/중지, 유효한 WDS 초대/연결 정보, 교수자 측 실제 연결 및 표시 어댑터가 필요합니다.
- `rdp://reverse/...` 문자열 생성이나 상태를 Connected로 바꾸는 것만으로 실제 연결이 생기지 않습니다.
- 테스트에서 `ReceiveFrame(byte[])`을 직접 호출한 결과는 전달 로직 검사입니다. 학생 창 이동/스크롤이 교수자 화면에 반영되는 근거와 구분합니다.
- 다중 학생은 연결별 viewer·수명·식별자를 분리하고, 한 학생 종료가 다른 학생 화면을 종료하지 않도록 합니다.
- 최종 배치는 5번에게 맡기되, 최소 호스트/실행 예제로 실제 표시가 가능한 어댑터를 먼저 증빙합니다.

### 5.2 교수자 → 선택 학생 실제 입력과 차단

- 마우스 이동 좌표, 버튼 종류/누름·뗌, 휠 변화량, 키 코드/누름·뗌을 실제 대상 연결까지 보존해야 합니다.
- 입력을 직접 메시지로 전달하는 설계라면 해당 데이터와 현재 세션/연결/제어 요청을 검증할 계약이 필요합니다. native viewer가 입력을 전달하는 설계라면 승인된 대상에만 전달되고 회수 시 막히는 경로를 증빙합니다.
- **입력 허용(권한 전환)**과 **개별 입력 이벤트 적용**은 같은 동작이 아닙니다. 학생 ID만 받는 허용 함수를 반복 호출한다고 좌표나 키가 전달되지는 않습니다.
- 결과를 기다리지 않고 Task를 버린 뒤 true를 반환하면 처리 실패를 알 수 없습니다. 성공/실패/취소 또는 큐 수락/실제 완료의 의미를 명시하고 호출자에 전달합니다.
- 엔진 미연결, 허용 실패, 제어 종료, 대상 전환, 허용 철회, 단절에서 실제 입력 결과와 상위 상태가 일치해야 합니다. 차단 실패 시 새 대상으로 넘어가거나 차단 완료로 표시하지 않습니다.
- 학생에게는 현재 제어 상태와 허용 철회/즉시 중단을 제공하는 기존 U07 정책을 유지합니다. 교수자 화면은 학생에게 보기 전용입니다.

### 5.3 판서 렌더링과 공유

- 스트로크 저장·Undo 스택·이벤트 발행 외에 실제 화면에 그리는 렌더러 및 학생에게 그 결과가 보이는 공유 경로가 필요합니다.
- 5번은 펜/도형/색/굵기/지우개 등 도구 모음 배치·입력 바인딩을 담당합니다. 실제 판서 동작/표시·공유 엔진 전체를 5번 대기로 넘기지 않습니다.
- 판서 ON/OFF는 그리기 입력 모드, 숨김은 가시성, 전체 삭제는 내용 삭제입니다. OFF만으로 내용이 사라지거나 기존 숨김 선택이 풀리지 않게 합니다.
- 표시/숨김/삭제/Undo 결과를 학생 수신에서 확인합니다. 동일 모니터의 공유 재시작 보존과 모니터 변경/강의 종료 초기화 정책은 기존 인계 기준을 따릅니다.

### 5.4 실제 WDS 화면 맞춤·휠 확대축소

- 배율/SourceRect 계산은 중간 결과입니다. 이를 실제 WDS viewer의 표시 표면에 적용하는 기술 어댑터와 최소 호출 예제를 제공해야 합니다.
- 5번은 창 크기/휠/버튼을 어댑터에 연결하고 컨트롤을 배치합니다. 가상의 `ApplyTransform` 호출 예시만으로 실제 적용 구현을 대신하지 않습니다.
- 실제 컨트롤의 STA/수명·DPI·모니터/해상도 변경을 고려하고, 선택한 방식이 실제 WDS 화면에 작동하는지 확인합니다.
- 적용 완료 이벤트는 실제 적용 성공 후에만 발행합니다. 미등록·실패·부분 실패를 상위가 알 수 있어야 합니다.

## 6. #51 과거 검토 기록 (head별 판단)

아래는 9/29에 검토한 이전 head `5dbd4d3`의 이력입니다. 최신 head의 현재 판정이 아니며, 후속 수정 유무와 테스트 수는 해당 리뷰 근거를 따릅니다. 이번 1·4번 후속 작업에서는 #51을 다시 검증하거나 수정하지 않았습니다.

- 해결 확인: 뷰어 등록만으로 ViewerApplied가 발생하던 오류, 뷰어 적용 실패 누락, 판서 활성화와 IsDrawing 상태 불일치. fix/test 커밋 분리도 확인했습니다.
- 미해결: 실제 native 입력 구현, 학생→교수자 화면 엔진, 판서 렌더링/공유, 실제 WDS 변환 적용.
- 새 입력 호출의 결함: ProcessMouseMove/Click/Wheel/KeyboardInput이 좌표·버튼·키 데이터 없이 `InjectInputAsync(participantId)`만 호출하고 Task를 버린 뒤 true를 반환합니다. 실패 입력 대역에서도 true 반환을 재현했습니다.
- 제품 파이프라인 구현이 Unavailable뿐이고 등록 핸들러 사용처가 테스트에만 있는 상태를 실제 엔진 완료로 표시하지 않습니다.
- 본문의 '#52 네이티브 파이프라인 대기', '실제 판서 렌더러 전체를 5번 대기' 분류는 위 역할 경계에 맞게 수정해야 합니다.
- PR head 단독 빌드 경고/오류 0, 476 통과·3 실제 WDS opt-in 건너뜀은 확인했습니다. 이는 실제 화면·입력 인수 증빙이 아닙니다.

## 7. 진짜 연계 대기와 독립 진행을 구분하는 방법

| 항목 | 필요한 협의/연계 | 기다리지 않고 진행할 수 있는 3번 작업 |
|---|---|---|
| 내부 초대/역방향 연결 정보의 자동 전달 | #58 보호 채널, #61 기존 단방향 자동 전달은 main 반영. 역방향/native 초대 wire는 방향/신원/수명을 1·2·3번이 별도 협의 | 기존 WDS 공급자/수신 구조 재사용 가능성을 확인하고 실제 공유·수신·초대 생성/폐기와 최소 연결 예제 제공 |
| 권한 승인/회수 | 이미 main에 있는 #52/#54 접점, 학생 보호 상태 메시지는 #58 | 실제 입력 게이트 구현, 승인/회수 실패·이전 연결 차단 테스트 |
| 표시 컨트롤 부착·판서 버튼 | 5번 UI STA/호스트/입력 바인딩 | 동작하는 표시/판서 엔진, 최소 호스트에서의 시각적 검증 및 초기화/해제 예제 |
| 파일/채팅과 실제 병행 | #59 파일 앱 연결까지 반영된 main 회귀 | 화면/입력 자체 검증과 종료 회귀. 파일 앱 연결을 전체 native 엔진 대기 사유로 삼지 않음 |

공통 계약이 부족하면 **필요한 입력/출력, 어느 프로세스에서 호출하는지, 대상 신원/세대, 성공·실패·취소, 비밀 정보 수명, 상대 담당**을 구체적으로 제안합니다. 1번 Core나 5번 MainWindow를 임의로 재설계하지 않습니다. 반대로 이러한 계약 조율이 필요하다는 이유로 자신의 실제 기술 구현을 다른 담당 책임으로 바꾸지도 않습니다.

## 8. 다음 제출 순서와 확인 기준

1. 최신 main 병합 → 본문에 기준 SHA와 충돌 해결 내역 기록.
2. 학생 1명 기준 실제 화면 수신·표시 → 교수자 입력 적용 → 제어 종료 후 차단을 우선 증빙. **최소 기술 증빙 순서일 뿐 최종 학생 2명 요구를 축소하지 않습니다.**
3. 실제 판서 표시/학생 수신, WDS 맞춤/휠·해상도 변화의 결과를 별도 증빙.
4. 학생 2명 독립 화면·한 대상 제어·대상 전환·단절/권한 철회·공유 재시작 회귀로 확대.
5. 5번에게 실제 클래스/초기화·부착·종료 API·실행 예제·오류 조건을 인계. UI 통합 후 파일/채팅 병행·15분·시연 2회 검수.
6. 구현(fix/feat), 회귀(test), 필요한 문서(docs)를 목적별로 커밋. 기존 #51에 푸시하고 원 작성자/본문을 유지하며 수정 요약을 댓글로 보충.

검증 기록에는 실행 PC/OS·화면 방향·코드 SHA·실행 순서·관찰 결과·실패 시 동작·대역 사용 여부를 남깁니다. 비밀번호·초대 비밀·학생 개인 화면은 공개 기록에 넣지 않습니다. 'API 호출됨'만 확인했다면 실제 OS 입력/렌더링 성공이라고 쓰지 않습니다.

## 9. 일정과 완료 표기

- 9월 4주차(9/21~27): #52/#53/#55 및 관련 서비스 기반을 반영하되 방 인증 앱 연결과 3번 최소 기술 검증 미달은 이월로 표시합니다.
- 9월 5주차(9/28~10/4): #54/#56/#57의 회수/라우팅/초대 복귀 기반을 활용해 남은 엔진·보호 연결·UI와 교차 검증을 진행합니다. 코드 머지를 주차 전체 완료로 바꾸지 않습니다.
- 10/1 검토본 → 10/2~4 수정·통합·인수 목표는 유지합니다. 역할별로 가능한 범위와 미완료/막힌 접점을 제출하고 인수 조건 미달은 명시합니다.
- 10/5 동결 판단, 10/25 내부 완료, 10/26~11/1 예비 주, 11/1 외부 목표는 그대로입니다. 현재 누락을 감춘 채 기간 내 완료를 보장하거나 QA 기간을 임의로 줄이지 않습니다.
- 이번 문서 업데이트는 새 기능 코드/설치형/ZIP 배포가 아닙니다. Releases의 기존 배포물과 main의 개발 진척을 구분합니다.
