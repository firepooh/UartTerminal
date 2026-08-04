# UartTerminal

ESP-IDF(ESP32) 개발용 **Serial UART 전용** 경량 터미널 (Windows 11, C#/WPF).
로그를 컬러로 보고, 창 크기에 맞게 재배치(reflow)하고, AI(Claude Code)가 MCP로 같은 포트를 읽고 쓸 수 있다.

> **문서 상태**: 구현 착수 전 계획 문서 (2026-07-21). 코드 없음.
> 모든 확인 항목([§6](#6-확정된-결정-사항)) 확정 완료. **사용자 지시가 있으면 Phase A부터 착수한다.**

## 1. 기능 범위

| # | 기능 | Phase |
|---|------|-------|
| 1 | UART 데이터 송수신 | A |
| 2 | 가변 창 크기 — 리사이즈 후 기존 출력도 새 폭에 맞게 재배치(reflow) | A |
| 3 | 내장 MCP 서버 — 사용자가 터미널을 쓰는 동안 AI가 TX/RX 가능, AI 송수신 데이터도 화면에 표시 | B |
| 4 | 다중 UART를 탭으로 — 필요 시 탭을 새 창으로 분리/다시 합치기 (Tier A: 메뉴/버튼, 단일 프로세스라 이동 중 연결 유지) | C |
| 5 | 통신 속도 선택 (포트 선택 시 프리셋에서) | C1a |
| 6 | 저장 명령 칩 바 — 자주 쓰는 **한 줄** 명령을 클릭 한 번으로 전송 (`commands.json`) | C2 |
| 7 | 이름 붙인 접속 프로필(세션) — 이름·포트·속도·열 때 리셋·개행·명령 그룹을 저장해 더블클릭 접속 (`sessions.json`) | C1b |

**확정 사항** (2026-07-21):

- ANSI 컬러 지원
- VT100(풀 에뮬레이션) 미지원 — 커서 절대이동/풀스크린 TUI(vim 등) 없음.
  단 ① 미지원 시퀀스는 파서가 **소비 후 무시**해서 화면에 깨진 문자가 새지 않게 하고,
  ② esp_console REPL을 위해 ESC[K / CR / 커서 위치 질의응답 3개는 예외적으로 처리한다 (§4.1, §6 Q2)
- 입력: **즉시 전송(type-through)** — 키를 누르는 즉시 송신 (§6 Q1)
- UI 프레임워크: **WPF** (굴림체 픽셀 동일성 요구 없음 — 폰트는 부드럽게 렌더링)
- 제품/폴더명: UartTerminal (오타 정정 완료)

**범위에서 제외** (1차 검토에 있었으나 축소로 삭제): 일반 설정 다이얼로그, 로깅. → [§8 비목표](#8-비목표-non-goals)
(C1a/C2에서 **통신 속도 선택**과 **한 줄 명령 저장**만 좁게 재도입됨 — 기능 5·6. 매크로/세션 프로필은 여전히 비목표)
(과거에 '다중 실행'은 별도 프로세스 여러 개로 비목표였으나, Phase C에서 **인-앱 탭 + 분리/합치기**로 재도입됨 — 기능 4)

## 2. 기본 설정값 (고정)

실행 시 **포트와 통신 속도만 선택**하고 나머지는 아래 값으로 고정한다.
(속도 예외의 근거: §3 DoD가 요구하는 921600bps 고속 로그, ROM 부트로더 진단용 74880bps — 실제로 115200 외가 필요하다.
그 외 항목을 열어주는 '설정 다이얼로그'는 여전히 비목표다 → [§8](#8-비목표-non-goals))

| 항목 | 값 | 비고 |
|------|-----|------|
| Speed | 115200 (기본) — 74880 / 230400 / 460800 / 921600 중 선택 | 포트 선택 다이얼로그, `state.json` 에 마지막 값 기억 |
| Data / Parity / Stop / Flow | 8 bit / none / 1 bit / none | 고정 |
| New-line | Receive: CR+LF(기본) / LF / CR / AUTO, Transmit: CR(기본) / CR+LF / LF | 탭별 · 세션 저장 (§2.1) |
| Local echo | OFF | |
| 수신 인코딩 | UTF-8 (증분 디코더) | ESP-IDF 로그 기준 |
| 폰트 | D2Coding 권장 (한글 2:1 고정폭), 없으면 Consolas+맑은 고딕 폴백 | |
| DTR / RTS (오픈 시) | 둘 다 deassert — **보드 리셋 안 함**(기본) | ESP32 오동작 방지 (§7 R2). 원할 때만 '열 때 리셋'(§2.2) |
| 스크롤백 | 10,000 논리 라인 (순환 버퍼) | |

### 2.1 개행(New-line) 규약

TeraTerm 의 [Setup > Terminal] New-line 에 대응한다. 장치마다 다르므로 속도·열 때 리셋과 같은
**탭(연결)별 접속 속성**이고 **세션에 함께 저장**된다: [터미널] > 개행(New-line) 은 활성 탭만 바꾸고,
기본값이 아닐 때 상태바에 `NL↓… ↑…` 로 표시되며, 세션 관리 화면의 `개행` 열에서 확인·편집한다.
세션이 값을 지정하지 않았거나(`(기본)`) 세션 없이 포트만 골라 열면 **마지막으로 쓴 값**(`state.json`)을 쓴다.

우리 화면 모델은 셀 격자가 아니라 **논리 라인 로그**라 LF 만 와도 계단 현상이 없으므로,
각 모드는 "어느 바이트를 개행으로 볼지"의 선택이다.

| 수신 모드 | CR | LF | 쓰는 경우 |
|---|---|---|---|
| **CR+LF** (기본) | 줄 처음으로(덮어쓰기) | 개행 | ESP-IDF 등 대부분. `\r` 진행바 갱신이 살아 있다 |
| LF | 무시 | 개행 | `\r` 로 덮어쓰지 않고 받은 순서대로 남기고 싶을 때 |
| CR | 개행 (뒤따르는 LF 흡수) | 무시 | CR 만 개행으로 쓰는 장치 |
| AUTO | 개행 | 개행 | CR·LF·CR+LF·LF+CR 어느 쪽이든 개행 1회(TeraTerm AUTO 규칙). 붙어 있는 쌍만 합치므로 `CR CR` 은 2줄, 단독 `\r` 덮어쓰기는 개행이 된다 |

송신 모드(CR / CR+LF / LF)는 **Enter 키 · 입력바 · 명령 칩 · 붙여넣기 · AI(`uart_send`)** 모든 경로에 같이 적용된다.

### 2.2 보드 리셋 / 부트로더 진입 (ESP32 devkit)

ESP32 개발보드는 USB-시리얼의 **DTR→IO0, RTS→EN** 이 트랜지스터 2개로 교차 결합돼 있다(자동 프로그램 회로).
`SerialPort.DtrEnable = true` 는 핀을 **LOW** 로 만들므로(assert) 회로도 진리표(1=HIGH)와는 반대로 읽어야 한다:

| Enable(dtr, rts) | 핀 (DTR, RTS) | EN | IO0 | 결과 |
|---|---|---|---|---|
| (false, false) | (1, 1) | 1 | 1 | 정상 실행 ← 오픈 시 기본 |
| (false, **true**) | (1, 0) | **0** | 1 | 리셋 걸림 |
| (**true**, false) | (0, 1) | 1 | **0** | 리셋 해제 + 부트로더 진입 |

- **하드웨어 리셋([보드]>하드웨어 리셋, Alt+R)**: `(false,true)` 100ms → `(false,false)` 50ms — esptool 의 classic reset 과 동일
- **부트로더 진입([보드]>부트로더 모드 진입, Alt+Shift+R)**: `(false,true)` 100ms → `(true,false)` 50ms → `(false,false)`
- **열 때 보드 리셋**(기본 꺼짐): 켜면 그 탭의 연결·자동 재연결·`uart_open` 모든 오픈 경로에서 리셋해
  부팅 로그를 처음부터 볼 수 있다. RX 루프를 띄운 뒤 펄스를 주므로 첫 줄부터 잡힌다.
  **속도와 같은 성격의 접속 속성**이라 전역이 아니라 **탭별** 값이고 **세션에 함께 저장**된다(보드마다 다르다):
  연결 다이얼로그 체크박스에서 고르고(세션을 선택하면 그 값으로 자동 채움), [보드] 메뉴로 현재 탭만 바꾸며,
  세션 관리 화면의 `열 때 리셋` 열에서 확인·편집한다. `state.json` 의 값은 새 탭의 기본값(= 마지막으로 쓴 값)일 뿐이다
- AI 경로는 MCP `uart_reset(bootloader)` — 타이밍이 보장돼 왕복 없이 한 번에 리셋한다

## 3. Phase 계획

### Phase A — UART 터미널 (기능 1, 2)

- 포트 선택 다이얼로그: friendly name 표시(WMI `Win32_PnPEntity`), 사용 중 포트 표시, 새로고침
- 수신 파이프라인 (§4.2): BaseStream.ReadAsync 루프 → 증분 디코더 → ANSI 파서 → 논리 라인 버퍼
- ANSI SGR 16색 렌더링 + CR/LF/BS 처리 + esp_console용 ESC[K/커서질의응답, 그 외 미지원 시퀀스 소비 (§4.1)
- reflow: 논리 라인 + lazy soft-wrap (§4.1), 전각(한글) 2셀 폭 처리
- 스크롤백 + **auto-scroll lock** (위로 스크롤 중 새 데이터 와도 뷰 고정, End로 복귀)
- 커스텀 렌더러 (GlyphRun 가상화, 30~60Hz 배칭, Per-Monitor V2 DPI)
- 키보드 입력 → TX: **즉시 전송(type-through)** — 키를 누르는 즉시 해당 바이트 송신 (§6 Q1). 키맵: 화살표=ESC[A~D(linenoise 히스토리 ↑↓/커서 ←→), Backspace=0x7F
- 복사/붙여넣기(드래그 선택=복사, 우클릭=붙여넣기), Clear screen/buffer 메뉴
- USB 핫플러그 크래시 방어: 분리 감지 → 창 제목 [끊김] → 수동 재연결(Alt+N)
- **자동 재연결**(기본 켬, [터미널] 메뉴 토글): USB 분리 후 같은 포트가 다시 나타나면 1.5초 폴링으로 감지해 조용히 재연결(팝업/포커스 뺏기 없음). 대기 중엔 상태바 안내 + 헤더 점 호박색. 사용자가 직접 끊은 경우(Alt+I)엔 동작 안 함
- 창 제목: `COM4:115200 - UartTerminal`
- 앱 진단 로그 최소 구현 (%LOCALAPPDATA%, 예외/포트 이벤트)

**DoD**: 케이블 뽑기 테스트 통과 / ESP-IDF 부팅 로그 컬러 정상 / 리사이즈 reflow 동작 / 921600bps 폭주 수신에서 UI 무응답 없음.

### Phase B — MCP 서버 (기능 3)

- **SDK**: 공식 C# SDK — NuGet [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
- **전송(권장, §6 Q4)**: 인스턴스별 Named Pipe(`\\.\pipe\uartterm-mcp-COM4`) + `StreamServerTransport` + 초소형 stdio 릴레이 exe.
  `.mcp.json`에 `claude mcp add uart-com4 -- UartTermMcp.exe COM4`로 정적 등록. 동적 포트/토큰/방화벽/Kestrel 문제가 모두 사라짐. 파이프 ACL은 현재 사용자 전용. GUI에 "등록 명령 복사" 버튼.
- **도구(9종)**: `uart_status`, `uart_send`(원자적 전송), `uart_read`(단조 증가 커서, 유실 시 `dropped_bytes` 명시, `strip_ansi` 기본 true), `uart_expect`(regex+timeout — polling 왕복 최소화), `uart_screen`, `uart_set_dtr_rts`, `uart_reset`(리셋/부트로더 진입 시퀀스 — §2.2), `uart_close`/`uart_open`(포트 양보/재점유 — esptool 등 외부 플래싱 도구용)
- **AI TX 화면 표시**: 수신 스트림에 섞지 않고 버퍼의 **메타 라인 타입**으로 삽입 (예: 회색 배경 `[AI→] ...`)
- **접근 제어**: MCP 활성/비활성 토글 + AI 읽기 전용(TX·제어·포트 열기/닫기 차단) 모드 + 상태바 인디케이터
- **포트 양보(플래싱)**: `uart_close`로 포트를 해제 → 셸에서 `esptool` 실행 → `uart_open`으로 재연결. 양보 중에는 자동 재연결이 중지되고 탭에 `[AI 양보]`로 표시.
- 포트 분리 상태를 에러 모델로 노출 (AI가 재시도 판단 가능하게)

> MCP 서버의 구조·등록·도구 레퍼런스·플래싱 워크플로우 상세: **[docs/MCP.md](docs/MCP.md)**

### Phase C1a — 통신 속도 선택 (기능 5)

- 포트 선택 다이얼로그에 속도 세그먼트(74880 / 115200 / 230400 / 460800 / 921600), 마지막 값은 `state.json`
- 접속 파라미터는 문서(탭)가 필드로 보유 → **수동 재연결 · 자동 재연결(USB 재접속) · MCP `uart_open` 3경로가 같은 속도**로 열림
- 속도 외 항목은 계속 고정(§2) — 특히 DTR/RTS 는 노출하지 않음(§7 R2)

### Phase C2 — 저장 명령 칩 바 (기능 6)

- `%APPDATA%\UartTerminal\commands.json` (`{schemaVersion, commands:[{name, text, confirm}]}`) — 사람이 읽고 diff 할 수 있는 형식.
  창 좌표 같은 휘발성 상태(`state.json`)와 **별도 파일**: 사용자 저작 콘텐츠이므로 `.bak` 백업 / 손상 시 `.corrupt-*` 보존 /
  읽기 실패 시 저장 잠금(덮어쓰기 방지) / 상위 `schemaVersion` 저장 거부
- 입력바 위 접이식 칩 스트립(**Alt+B**, 전역 설정). 칩은 `Focusable=false` — 클릭 직후 타이핑이 그대로 터미널로 나가야 하므로 필수
- **클릭 = 전송**(입력바와 동일한 단일 TX 큐·CR 규약), **Ctrl+클릭 = 입력창에 채우기**, `confirm` 명령은 전송 전 확인
- 편집은 모달 평면 목록(폴더 트리·드래그 정렬 없음) + "현재 입력을 명령으로 저장"
- 팀 공유 = `commands.json` 파일 복사(편집기의 "폴더 열기"). export/import UI·경로 전환은 두지 않음
- **한 줄 전송까지만** — 개행을 데이터 계층에서 제거해 다단계 시퀀스가 섞이는 것을 차단(§8)

### Phase C1b — 이름 붙인 접속 프로필 (기능 7)

- `%APPDATA%\UartTerminal\sessions.json`
  (`{schemaVersion, sessions:[{name, port, baud, resetOnOpen?, newlineRx?, newlineTx?, commandGroup?}]}`)
  — 접속을 재현하는 데 필요한 값만 저장. 8N1/흐름제어는 고정이고 오픈 시 DTR/RTS deassert 도 고정이다 —
  리셋은 '펄스를 줄지'라는 의도라 `resetOnOpen` 한 항목으로만 노출한다(§2.2).
  **기본값/미지정 필드는 파일에 쓰지 않는다**(사람이 읽는 파일을 조용하게). `newline*` 이 없으면 '지정 없음' →
  접속 시 마지막으로 쓴 값을 유지하며, **알 수 없는 이름이 적혀 있어도 예외 대신 '지정 없음'** 으로 떨어뜨린다
  (오타 하나로 프로필 전체가 `.corrupt-*` 로 격리되지 않게)
  손실 방어는 `commands.json` 과 동일(원자 저장 + `.bak`, 손상 시 `.corrupt-*`, 읽기 실패 시 저장 잠금,
  상위 `schemaVersion` 저장 거부, 저장 성공 시에만 목록 커밋)
- 연결 다이얼로그를 **확장**(교체 아님 — 취소 안전 계약을 지키기 위해): 위쪽에 세션 목록(더블클릭 접속·삭제),
  아래에 감지된 포트 목록. **세션이 없으면 세션 섹션은 숨겨져** 기존 화면과 동일하다
- 세션은 '폼을 채우는 바로가기'이고 확정 값은 항상 폼에서 읽는다(진실의 출처 하나). 세션의 포트가 지금 없으면
  그 포트명으로 열기를 시도해 기존 자동 재연결 대기로 이어진다
- 저장은 [터미널 > 현재 연결을 세션으로 저장…] — 현재 탭의 포트·속도를 이름과 함께 기록(같은 이름이면 갱신)
- 같은 보드를 속도만 달리해 두 프로필로 두는 사용(부트 진단 74880 / 로그 921600)이 정상 지원된다
- **폴더 트리·드래그 정렬·VID:PID 자동 매칭·탭 세트 복원은 넣지 않았다**(§8) — COM 번호가 바뀌는 환경에서
  자동 매칭은 동일 어댑터 2개일 때 엉뚱한 보드에 조용히 연결될 위험이 있어 v1 범위에서 제외

## 4. 핵심 아키텍처

### 4.1 reflow → "논리 라인 버퍼" 모델

고정 셀 그리드(진짜 VT100 방식)로는 리사이즈 재배치가 불가능하다 (TeraTerm도 미지원). 따라서:

- 버퍼 단위 = 개행으로 구분된 **논리 라인** { 텍스트, 스타일 run(색), 라인 타입(일반/AI 메타) }
- 렌더 시점에 현재 창 폭으로 soft-wrap (lazy 계산 + 캐시) → reflow가 "렌더 폭 변경"으로 해결
- 전각 문자는 UAX #11 기반 폭 함수 하나로 2셀 판정 (래핑/선택/커서가 모두 이것만 참조)
- ANSI 파서는 상태 머신:
  - **반영**: SGR 컬러(16색+bright, bold, reset) → 스타일 run
  - **예외 처리(esp_console REPL용, §6 Q2)**: ESC[K(줄 지우기), CR, 커서 위치 질의 ESC[6n → ESC[row;colR 응답 송신. "열린 마지막 라인" 편집에 한정하면 논리 라인 모델과 충돌 없음
  - **소비 후 무시**: 그 외 전부(커서 절대이동 CUP, 스크롤 영역, alternate screen 등) — 반쪽 처리하면 화면 오염
- 스크롤바 좌표계는 논리 라인 인덱스 기준 → 리사이즈 시 전체 재계산 불필요

### 4.2 데이터 파이프라인 — MCP 훅을 Phase A에 선반영

Windows COM 포트는 **한 프로세스만** 열 수 있으므로 MCP 서버는 반드시 in-process. Phase A 파이프라인에 분기점이 없으면 Phase B에서 갈아엎게 된다.

```
시리얼 RX ──[ReadAsync 루프(전용 워커, 예외 격리)]──> Channel<byte[]>
                                                        │
                              ┌────── tee ──────────────┤
                              ▼                         ▼
                    [MCP용 링버퍼 (Phase B)]   [증분 디코더 → ANSI 파서 → 논리 라인 버퍼]
                                                        ▼
                                             [커스텀 렌더러 (배칭 30~60Hz)]

키 입력 ┐
붙여넣기 ├──> [단일 TX 큐 (직렬화, AI 전송 1회는 원자적)] ──> 시리얼 TX
AI 전송 ┘
```

- `SerialPort.DataReceived` 이벤트 사용 금지 (§7 R1)
- 수신 이벤트당 UI 갱신 금지 — 배칭 필수 (§7 R4)
- WPF 기본 텍스트 컨트롤(TextBox/RichTextBox) 사용 금지 — 프로토타입에도 쓰지 말 것
- 시리얼 I/O는 `ISerialSession` 인터페이스로 격리 (테스트 fake 주입)

## 5. 요구사항에 없지만 포함해야 하는 것 (검토 결과)

| 항목 | 이유 | Phase |
|------|------|-------|
| 스크롤백 + auto-scroll lock | 없으면 부팅 로그를 거슬러 볼 수 없음 — 터미널로 성립 불가 | A |
| USB 핫플러그 크래시 방어 | .NET SerialPort는 케이블 분리 시 프로세스가 죽는 알려진 문제 | A |
| UTF-8 증분 디코더 + 전각 폭 | 없으면 한글이 주기적으로 깨지고 reflow/선택이 어긋남 | A |
| DTR/RTS 오픈 시 무간섭 | 잘못 잡으면 터미널 켤 때마다 ESP32가 리셋/부트모드 진입 | A |
| 미지원 이스케이프 소비 | "VT100 미지원"이어도 파서는 필요 — 안 삼키면 화면이 깨진 문자로 오염 | A |
| 복사/붙여넣기, 클리어, 창 제목, friendly name | 최소 UX — 없으면 TeraTerm에서 못 갈아탐 | A |
| 최소 지속성 (마지막 포트, 창 크기) | 설정 기능을 뺐으므로 이것도 없음 — 매번 포트 선택+기본 창이 됨 (§6 Q3) | A |
| 같은 포트 중복 오픈 에러 처리 | 다중 실행을 "지원"하지 않아도 exe 2번 실행은 막을 수 없음 | A |
| MCP용 링버퍼 | 로깅을 뺐어도 AI가 읽을 수신 버퍼는 별개로 필요 | B |
| 앱 진단 로그 | "가끔 수신이 멈춘다" 추적 수단 (데이터 로깅과 별개) | A |

## 6. 확정된 결정 사항

모든 확인 항목이 확정되었다 (2026-07-21). 아래 값으로 Phase A를 착수한다.

| # | 항목 | 결정 |
|---|------|------|
| Q1 | 입력 방식 | **즉시 전송(type-through)** — 키를 누르는 즉시 해당 바이트 송신 |
| Q2 | esp_console REPL 지원 | **지원** — ESC[K(줄 지우기) / CR / 커서 위치 질의응답(ESC[6n→ESC[row;colR) 3개를 예외적으로 처리해 linenoise 라인 편집·명령 히스토리(↑↓) 동작 |
| Q3 | 마지막 포트/창 크기 기억 | **둔다** — `%APPDATA%\UartTerminal\state.json` (설정 다이얼로그와는 별개의 최소 상태 파일) |
| Q4 | MCP 전송 방식 | **Named Pipe + stdio 릴레이 exe** (정적 등록, 무토큰, Kestrel 불필요) |
| Q5 | 런타임 | **.NET 10 LTS**, self-contained single-file(win-x64) 배포 |

## 7. 주요 리스크

| # | 리스크 | 대응 |
|---|--------|------|
| R1 | SerialPort 핫플러그 크래시([dotnet/runtime#20821](https://github.com/dotnet/runtime/issues/20821)), DataReceived 데드락 | BaseStream.ReadAsync 루프 + 예외 정규화. 케이블 뽑기를 회귀 테스트에 포함 |
| R2 | 포트 오픈 시 DTR/RTS 상태로 ESP32 의도치 않은 리셋/부트모드 진입 | 기본 deassert. CP210x 실기 테스트 |
| R3 | 그리드 버퍼로 시작하면 reflow 후장착 불가(전면 재작성) | §4.1 논리 라인 모델을 처음부터 채택 |
| R4 | 고속 수신(921600bps~) 시 UI 스레드 포화 | §4.2 배칭 파이프라인, 문자 단위 갱신 금지 |
| R5 | Phase A에 tee/단일 TX 큐/메타 라인 타입이 없으면 Phase B(MCP)에서 파이프라인 재작성 | §4.2 구조를 Phase A에 선반영 |
| R6 | MCP 동적 포트 HTTP ↔ `.mcp.json` 정적 등록 불일치 | Named Pipe + 릴레이 exe로 원천 회피 |

## 8. 비목표 (Non-goals)

**검토 후 의도적으로 제외** (누락 아님):

- **일반 설정 다이얼로그** — 에뮬레이션/외관/개행/인코딩/흐름제어 등을 세션별로 열어주는 창.
  (C1a에서 **통신 속도만** 예외로 열었다 → §2. DTR/RTS 는 ESP32 오리셋 방지 안전장치(§7 R2)라 계속 비노출)
- **다단계 매크로** — 시퀀스/지연/조건 분기/expect 스크립트, 명령 변수 치환.
  (C2의 저장 명령은 **한 줄 문자열 전송까지만**이며 데이터 계층에서 개행을 제거해 이 경계를 강제한다.
  순차 전송·응답 대기 자동화는 MCP `uart_send`/`uart_expect` 가 담당 — [docs/MCP.md](docs/MCP.md))
- 세션 프로필의 **폴더 트리·드래그 정렬**, **VID:PID 기반 포트 자동 매칭**, **탭 세트 복원**
  (프로필 자체는 C1b 에서 3필드로 도입됨 — 기능 7. 부팅 시 자동 포트 점유는 esptool 플래싱과 경합하므로 신중)
- 로깅 기능, 명령줄 인자
- 풀 VT100 에뮬레이션 / 풀스크린 TUI (vim, menuconfig)
- 256색/트루컬러 (16색+bright로 시작, 필요 시 확장)
- 파일 전송(XMODEM류/Send file), telnet/ssh, 검색, 화면 타임스탬프
- 크로스플랫폼, 다국어, 자동 업데이트

축소 전 전체 검토(설정/로깅/다중실행 포함 5개 관점 분석)는 git 이력의 이전 README 버전 참조.
SecureCRT 식 세션/커맨드 관리 기능의 도입 범위 검토(무엇을 왜 채택/기각했는지)는 C1a/C2 커밋 메시지 참조.

## 9. 기술 스택

| 항목 | 채택 |
|------|------|
| 런타임 | .NET 10 LTS (§6 Q5) |
| UI | WPF + 커스텀 GlyphRun 렌더러 (확정) |
| 시리얼 | System.IO.Ports (NuGet) + BaseStream 루프. 문제 시 RJCP.SerialPortStream로 교체 가능하게 `ISerialSession` 격리 |
| MCP | ModelContextProtocol (공식 C# SDK) + Named Pipe |
| 테스트 | xUnit — ANSI 파서/논리 라인 버퍼/전각 폭 단위 테스트, esp_idf_monitor 실출력 골든 파일, 실기 체크리스트(케이블 뽑기, 921600bps) |

## 10. 폴더 구조 (예정)

```
UartTerminal/
├─ README.md
├─ src/
│  ├─ UartTerminal/             # WPF 앱
│  │  ├─ Serial/                # ISerialSession, TX 큐, 핫플러그 감지
│  │  ├─ Terminal/              # 증분 디코더, ANSI 파서, 논리 라인 버퍼
│  │  ├─ Rendering/             # 커스텀 렌더러 (GlyphRun, 가상화)
│  │  └─ Mcp/                   # MCP 서버 (Phase B)
│  └─ UartTerminal.McpRelay/    # stdio↔Named Pipe 릴레이 exe (Phase B)
└─ tests/
   └─ UartTerminal.Tests/
```

## 참고 자료

- .NET SerialPort 핫플러그 크래시: [dotnet/runtime#20821](https://github.com/dotnet/runtime/issues/20821)
- MCP C# SDK: [GitHub](https://github.com/modelcontextprotocol/csharp-sdk) / [Claude Code MCP 등록](https://code.claude.com/docs/en/mcp)
- reflow 참고: [Windows Terminal PR #4741](https://github.com/microsoft/terminal/pull/4741)
- ESP32 리셋 시퀀스 참조: 로컬 `esp_idf_monitor/base/reset.py` (Apache-2.0)
