# 실행 및 설정 가이드

이 문서는 EduStream을 실행할 때 필요한 최소 절차만 정리합니다.

## 1. 실행 위치

`EduStream.sln` 파일이 있는 폴더에서 명령어를 실행합니다.

파일 탐색기에서 해당 폴더를 열고 주소창에 `cmd`를 입력하면 그 위치에서 명령 프롬프트가 열립니다.

## 2. 빌드

처음 실행하거나 코드를 받은 뒤에는 먼저 빌드합니다.

```bash
dotnet build EduStream.sln
```

## 3. 서버 실행

첫 번째 cmd 창에서 아래 명령어를 입력합니다.

```bash
dotnet run --project src/EduStream.Server/EduStream.Server.csproj
```

`EduStream Server` 창이 뜨면 정상입니다.

서버 기본값:

- Session Name: `Capstone Live Class`
- Port: `5000`

## 4. 클라이언트 실행

두 번째 cmd 창을 새로 열고 아래 명령어를 입력합니다.

```bash
dotnet run --project src/EduStream.Client/EduStream.Client.csproj
```

`EduStream Client` 창이 뜨면 정상입니다.

클라이언트 기본값:

- 표시 이름: `StudentDemo`
- 서버 IP: `127.0.0.1`
- 포트: `5000`

같은 PC에서 서버와 클라이언트를 같이 실행할 때는 서버 IP를 `127.0.0.1`로 둡니다.

## 5. 기본 실행 순서

1. 서버 실행
2. 서버 창에서 `Open Session` 클릭
3. 클라이언트 실행
4. 클라이언트 창에서 서버 IP와 포트 확인
5. 클라이언트 창에서 `세션 참가` 클릭
6. 연결 상태와 Activity Log 확인

## 6. 서버 버튼 기능

- `Open Session`: 강의 세션을 엽니다.
- `Close Session`: 열린 세션을 종료합니다.
- `Send Preview Frame`: 화면 프리뷰 프레임을 한 번 전송합니다.
- `Start Auto Share`: 화면 프레임 자동 송신을 시작합니다.
- `Stop Auto Share`: 화면 프레임 자동 송신을 중지합니다.
- `Send Sample File`: 샘플 파일을 청크 단위로 전송합니다.
- `Send Message`: 서버에서 채팅 메시지를 전송합니다.
- `Start RDP`: RDP 테스트 연결을 시작합니다.
- `Stop RDP`: RDP 테스트 연결을 중지합니다.

RDP 기능은 테스트용입니다. 기본 세션, 채팅, 파일 전송, 화면 송신 확인에는 필수로 사용하지 않아도 됩니다.

## 7. 클라이언트 버튼 기능

- `세션 참가`: 입력한 서버 IP와 포트로 세션에 접속합니다.
- `연결 종료`: 현재 세션 연결을 종료합니다.
- `Send Message`: 클라이언트에서 채팅 메시지를 전송합니다.

## 8. 테스트

빌드 후 테스트를 실행할 때 사용합니다.

```bash
dotnet test EduStream.sln --no-build
```

## 9. 연결이 안 될 때 확인할 것

- 서버에서 `Open Session`을 먼저 눌렀는지 확인
- 서버 Port와 클라이언트 Port가 같은지 확인
- 같은 PC가 아니면 클라이언트 IP를 `127.0.0.1`로 두지 않았는지 확인
- Windows 방화벽에서 포트 `5000`이 막혀 있지 않은지 확인
