# 1·4번 최종 단계 선행 구현 인계

작성: 2026-09-20. 코드 반영 기준 main `ce1dd3a` (#49).
**#48(1번) → #49(4번) 순서로 main 반영 완료. 실제 UI/네트워크/RDP 연동 및 제품 배포 완료와는 구분합니다.**
역할 브랜치: `feature/final-core-readiness`, `feature/final-file-readiness`. 두 코드 PR 병합 후 문서·진척을 갱신합니다.
9월 이후 주차별 일정은 유지하고, 독립 구현·자동 회귀·검수 준비를 앞당깁니다. 실제 연동과 최종 인수는 별도입니다.

## 1. 준비 범위와 담당 경계

| 배치 | 1번 준비 | 4번 준비 | 이후 인수 조건 |
|---|---|---|---|
| 9월 3주차 | 참가 신원/연결 ID·목록 revision, 제어 요청/승인/회수 상태·오류, 공유 세대 계약 | 등록/해제·요청/청크/저장 결과 계약 | 2번과 보안 채널/인증·wire 협의, 3번 native 어댑터 대조 |
| 9월 4주차 | 독립 상태 검증, UI용 모의 세션/제어 대역 | 등록/목록/해제·요청형 송신·다운로드 폴더 저장·중복 이름 보존 | 2번 라우팅, 5번 양 앱 UI 연결 |
| 9월 5주차 | 이전 연결/권한 revision 거부, 공유 중지/재시작·판서 OFF/숨김 계약 회귀 | 변경 원본·해시/누락·중복·취소·등록 해제·동시 요청 회귀 | 3번 실제 화면/입력, 2번 승인/취소 전달과 교차 기능 검수 |
| 10월 1~2주차 | 반복 실행 도구·실행 증빙 해시·QA 체크리스트 | 파일 검증 자동화·실패 복구/재시도 근거 | 실제 서비스 조립 후 QA 수행 및 담당별 결함 수정 |
| 10월 3~4주차 | 최종 인수/배포·문서 기록 양식 | 실제 파일/제어/화면 병행 검수 양식 | 10/25 내부 완료 판단, 마지막 주 피드백·최종 배포 검수 |

10월 QA/최종 검수를 미리 완료한 것은 아닙니다. 이번 준비로 다른 담당자 구현을 대체하지 않습니다.

## 2. 코드 위치와 호출 책임

- Core: `src/EduStream.Core/Collaboration/`, `src/EduStream.Core/FileSharing/`
- 4번 송신: `src/EduStream.Server/Services/SessionFileCatalog.cs`
- 4번 수신: `src/EduStream.Client/Services/SessionFileDownloader.cs`, `DownloadsDirectory.cs`
- 개발 전용 대역: `tools/EduStream.ContractTestDoubles/` (Core만 참조, 제품 프로젝트에서 참조하지 않음)
- 테스트: `tests/EduStream.FileTransfer.Tests/FinalReadiness*.cs`

### 2번이 연결할 부분

1. 승인된 **실제 연결**에서 ParticipantConnection을 생성합니다. 표시 이름·수신 SenderId·클라이언트 제공 역할을 신뢰하지 않습니다. 재참가/연결 교체마다 ConnectionId를 새로 발급합니다.
2. IRoomSessionClient, IRemoteControlCoordinator, IFileRequestAuthorizer를 구현합니다. 후자는 저장된 참가자/연결/세션 수명과 매번 대조하며 알 수 없으면 false입니다.
3. 목록은 RoomJoined의 단조 증가 revision으로, 보기/제어 허용 변경은 PermissionRevision으로 전달합니다. 접속/권한 상태는 UI 데이터가 아니라 서버 기준으로 재검증합니다.
4. 파일 catalog는 강의당 하나. 등록/해제는 교수자 로컬 작업만 허용합니다. 학생 요청은 파일 ID/버전만 받으며 로컬 경로를 받지 않습니다.
5. DownloadAsync에서 받은 청크를 요청한 연결 하나에만 순차 전송합니다. 무제한 큐나 전체 청크 목록을 만들지 않습니다. 최종 완료 응답/실패는 RequestId로 묶고, 실제 저장 완료 전에는 성공 ACK를 보내지 않습니다.
6. 연결 종료/세션 종료/사용자 취소 시 송수신 CancellationToken을 취소합니다. 파일 등록 해제 알림을 받은 수신 측의 진행 중 요청도 취소합니다.
7. 공유 일시 중지는 강의 종료와 분리합니다. 내부 초대 재발급은 자동으로 처리하되 과거 sharing generation/connection 콜백을 거부합니다.
8. 원격 제어 단일 대상 전환은 기존 native 입력 회수 확인 → 새 승인/요청 순서입니다. 모델의 Revoke만 호출하고 입력이 차단됐다고 보고하지 않습니다.

### 보안·wire: 아직 조율/구현이 필요한 접점

- 신규 DTO/인터페이스 및 별도 CollaborationMessageCodec을 제공합니다. 기존 PacketType 1~11/ProtocolVersion/직렬화 경로를 바꾸거나 신규 타입을 legacy FilePacket으로 위장하지 않았습니다.
- 신규 능력 식별은 `edustream.collaboration/1`. 신규 JSON envelope는 Version=1, MessageId, Kind, Payload이며 목록/파일 요청·청크·취소·저장 완료/실패를 지원합니다. 프레임 최대 2 MiB, JSON 깊이 16, 중복 키·잘못된 kind/version/식별자·메타데이터를 거부합니다. FileStoredNotice에는 학생 로컬 경로를 넣지 않습니다.
- 2번은 보호 채널에서 capability 협상 후 해당 codec과 길이 프레이밍/dispatcher를 연결해야 합니다. **수신 길이를 메모리 할당 전에 제한**하고 저장 완료 메시지를 원 요청/인증된 학생과 대조해야 합니다. 승인/원격 제어/native 초대 메시지, timeout 및 실제 네트워크 종료/취소는 추가 계약·구현·회귀가 필요합니다.
- 보호된 채널에서 교수자 신원을 검증한 뒤 방 비밀번호와 내부 초대 비밀을 전달해야 합니다. 비밀번호가 없는 방도 교수자 검증을 생략하지 않습니다.
- **TLS/인증서 신뢰 등록·갱신, 재연결 인증 보관/만료, password 검증/시도 제한의 구체 구현은 2번과 확인 전 미완료**입니다. 인증서 오류 무시·accept-all 검증·평문 TCP fallback은 허용하지 않습니다.
- 이 문서만으로 방 인증/자동 재접속 구현 완료라고 표시하지 않습니다. 현재 실행 앱은 기존 연결 경로 그대로입니다.

### 3·5번이 연결할 부분

- SharingLifecycleState.Started는 실제 native 성공에서만 호출. Begin/메서드 반환만으로 성공 처리하지 않습니다.
- RemoteControlState는 계약 검증 모델일 뿐 native 입력 차단 장치가 아닙니다. 2번이 요청/대상 단일성을 보장하고 3번이 실제 허용/차단을 수행합니다.
- ISharedScreenPresentation은 fit/zoom/pan 접점, IAnnotationController는 판서 버튼 접점입니다. COM 컨트롤/부착 방법·초대 비밀 전달은 기존 계약과 3번 구현을 대조하여 확정합니다.
- 판서 OFF는 Drawing만 false, 숨김은 Visible만 전환. ClearAndStop은 내용 삭제 revision과 Drawing=false를 함께 적용합니다. 실제 도형/이력 저장·Undo는 3번 책임입니다.
- 5번은 파일 선택/드롭 → RegisterAsync, 삭제 → Unregister, 파일명/버튼 → 새 RequestId 다운로드로 연결합니다. 채팅/파일 UI 파일은 이번 작업에서 수정하지 않았습니다.
- 대역의 Join/ConfirmNativeControlForTest는 **모의 결과**입니다. 실제 RDP/인증 증빙이나 배포 구성에 넣지 않습니다. 테스트 프로젝트만 참조하고 필요하면 별도 UI 개발 전용 프로젝트에서 사용합니다.

## 3. 파일 세부 정책

- 등록은 파일 이름·길이·SHA256·청크 크기만 공유합니다. 본문 전송은 요청 때 시작합니다.
- 원본은 교수자 파일을 그대로 보존합니다. 등록 시 SHA256, 다운로드 시 읽기 잠금 상태로 크기/해시를 재확인합니다. 변경됐다면 SourceChanged이며 재등록이 필요합니다. 새 파일 ID로 이전 요청과 분리합니다.
- 파일 ID는 강의 내 불변 등록 항목입니다. 삭제 후 재등록은 새 ID, 항목 revision은 현재 1. catalog revision은 추가/삭제마다 증가합니다.
- 등록 해제/권한 철회는 이후 청크와 최종 송신 완료를 차단합니다. 이미 전송 큐/네트워크에 넘긴 바이트는 회수 불가하므로 2번은 수신 취소도 전달해야 합니다. 이미 원자적으로 저장 완료한 파일은 유지합니다.
- 수신은 요청/강의/파일/revision/index/정확한 청크 길이를 검사하고 임시 파일에 순차 저장합니다. 신뢰된 TCP 위에서 순서가 맞는 단일 요청 스트림이 전제입니다. 중복/역순/누락/해시 오류는 실패하며 새 RequestId로 처음부터 재시도합니다. 레거시 out-of-order 조립 경로는 유지됩니다.
- SHA256 및 정상 EOF를 확인한 뒤 같은 디렉터리에서 overwrite:false로 이름을 확보합니다. 동명은 `파일 (1).확장자` 방식, 기존 파일/디렉터리는 덮어쓰지 않습니다.
- 현재 새 경로 제한: 파일 512 MiB, 등록 100건, 서비스 인스턴스당 동시 요청 8건, 중복 이름 1000번까지. 제한은 메모리/자원 보호이며 이 크기/인원의 실측 성능 보장이 아닙니다. 8 MiB까지 자동 바이트 회귀를 수행합니다.
- 실패/취소에는 해당 요청의 임시 파일만 정리합니다. 프로세스 강제 종료/전원 중단 시 partial 잔존 가능: 다른 앱/진행 중 파일까지 자동 삭제하지 않으며 최종 배포 정책 검수 대상입니다.
- 다운로드 경로는 [Windows Known Folder API](https://learn.microsoft.com/en-us/windows/win32/shell/known-folders)로 조회합니다. 이동된 다운로드 폴더도 조회하며, 실제 경로 조회 실패/쓰기 권한 오류는 다른 폴더로 몰래 우회하지 않습니다.
- 예외 UI 표시는 CollaborationErrorCatalog 사용. 원본 예외 메시지/로컬 경로·비밀번호를 그대로 표시/송신하지 않습니다.

## 4. 미세 동작 제안과 확인 상태

다음은 선행 구현/연동 기준이며 관련 담당자가 구현 가능성과 이견을 검토해야 합니다.

| 항목 | 기준 | 상태 |
|---|---|---|
| 파일 해제 중 전송 | 신규/후속 청크 차단, 수신 취소, 이미 저장된 파일 보존 | 4번 로컬 구현/회귀, 취소 라우팅 대기 |
| 학생 화면 접기 | 보기 표시만 접음. 제어 중 대상을 접으면 먼저 제어 종료·상태 표시 | 2·3·5번 협의/실제 구현 대기 |
| 모두 접기 | 활성 제어를 먼저 종료하고 카드 접기 | 같은 상태 |
| 판서 모니터 변경/강의 종료 | 판서/Undo 이력을 초기화, 다른 모니터에 이전 좌표를 재사용하지 않음 | 3·5번 구현 대기 |
| 판서 공유 일시 중지 | 동일 강의·동일 모니터이면 내용 보존, 재시작 시 재표시 | 3번 native 확인 대기 |
| 실패/재시도 | 연결/권한 세대와 요청 ID 새로 확인, 거부/퇴장 후 임의 자동 복구 금지 | 순수 계약 회귀, 실연동 대기 |

추가 기능으로 자동 확대하지 않고 현재 담당 경계와 사용자 합의가 우선합니다.

## 5. 재현과 검수

2026-09-20 게시 전 재검증: 각 역할의 깨끗한 작업본에서 확인했습니다. 코어 단독 **220 통과 / 3건 건너뜀**, 코어+파일 신규 **67/67을 2회 연속 통과**, 전체 **253 통과 / 실제 WDS 선택 실행 3건 건너뜀 / 실패 0**. Debug/Release 빌드 경고 0/오류 0. 기존 186개와 신규 67개를 함께 실행했으며 실제 WDS opt-in은 이번에 실행하지 않았습니다. 재검토에서 빈/누락 payload 거부와 회귀 2건을 추가했습니다.

증빙 묶음은 실행 도구가 생성한 `readiness-1.trx`, `readiness-2.trx`, `full.trx`, `evidence.json`입니다. evidence.json에는 기준 커밋과 미커밋 목록·소스 SHA256이 포함되어 미커밋 코드를 기준 main 성공으로 혼동하지 않도록 했습니다. 로컬 결과 경로는 작업 보고에서 전달하며 저장소에 개인 절대 경로를 넣지 않습니다.

```powershell
./scripts/Test-CoreFileReadiness.ps1 -Repeat 2
```

빌드 → 신규 회귀 2회 → 기존 전체 회귀. 결과 경로에 TRX와 소스별 SHA256/미커밋 상태를 기록합니다. 실제 WDS opt-in은 켜지 않습니다. 실행 중인 앱을 종료/재실행하지 않습니다.

호출 예제는 FinalReadinessFileDownloadTests.RequestDownload_StreamsToDiskWithExactBytes와 ReadinessFiles를 참고합니다. 실제 송수신 대신 async enumerable을 직접 연결한 **모의 연동**임을 명시합니다.

[최종 검수 체크리스트](./FINAL_ACCEPTANCE_CHECKLIST.md)의 빈 칸은 실제 결과가 생길 때만 채웁니다. UI/네트워크/RDP/설치형·ZIP 및 발표자료를 구현 완료로 표시하지 않습니다.
