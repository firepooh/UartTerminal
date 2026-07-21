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

**확정 사항** (2026-07-21):

- ANSI 컬러 지원
- VT100(풀 에뮬레이션) 미지원 — 커서 절대이동/풀스크린 TUI(vim 등) 없음.
  단 ① 미지원 시퀀스는 파서가 **소비 후 무시**해서 화면에 깨진 문자가 새지 않게 하고,
  ② esp_console REPL을 위해 ESC[K / CR / 커서 위치 질의응답 3개는 예외적으로 처리한다 (§4.1, §6 Q2)
- 입력: **즉시 전송(type-through)** — 키를 누르는 즉시 송신 (§6 Q1)
- UI 프레임워크: **WPF** (굴림체 픽셀 동일성 요구 없음 — 폰트는 부드럽게 렌더링)
- 제품/폴더명: UartTerminal (오타 정정 완료)

**범위에서 제외** (1차 검토에 있었으나 축소로 삭제): 설정 저장/로드 다이얼로그, 다중 실행 공식 지원, 로깅. → [§8 비목표](#8-비목표-non-goals)

## 2. 기본 설정값 (고정)

실행 시 **포트만 선택**하고 나머지는 아래 값으로 고정한다.

| 항목 | 값 | 비고 |
|------|-----|------|
| Speed / Data / Parity / Stop / Flow | 115200 / 8 bit / none / 1 bit / none | |
| New-line | Receive: CR, Transmit: CR | |
| Local echo | OFF | |
| 수신 인코딩 | UTF-8 (증분 디코더) | ESP-IDF 로그 기준 |
| 폰트 | D2Coding 권장 (한글 2:1 고정폭), 없으면 Consolas+맑은 고딕 폴백 | |
| DTR / RTS (오픈 시) | 둘 다 deassert — **보드 리셋 안 함** | ESP32 오동작 방지 (§7 R2) |
| 스크롤백 | 10,000 논리 라인 (순환 버퍼) | |

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
- USB 핫플러그 크래시 방어: 분리 감지 → 창 제목 [disconnected] → 수동 재연결
- 창 제목: `COM4:115200 - UartTerminal`
- 앱 진단 로그 최소 구현 (%LOCALAPPDATA%, 예외/포트 이벤트)

**DoD**: 케이블 뽑기 테스트 통과 / ESP-IDF 부팅 로그 컬러 정상 / 리사이즈 reflow 동작 / 921600bps 폭주 수신에서 UI 무응답 없음.

### Phase B — MCP 서버 (기능 3)

- **SDK**: 공식 C# SDK — NuGet [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
- **전송(권장, §6 Q4)**: 인스턴스별 Named Pipe(`\\.\pipe\uartterm-mcp-COM4`) + `StreamServerTransport` + 초소형 stdio 릴레이 exe.
  `.mcp.json`에 `claude mcp add uart-com4 -- UartTermMcp.exe COM4`로 정적 등록. 동적 포트/토큰/방화벽/Kestrel 문제가 모두 사라짐. 파이프 ACL은 현재 사용자 전용. GUI에 "등록 명령 복사" 버튼.
- **도구**: `uart_status`, `uart_send`(원자적 전송), `uart_read`(단조 증가 커서, 유실 시 `dropped_bytes` 명시, `strip_ansi` 기본 true), `uart_expect`(regex+timeout — polling 왕복 최소화), `uart_screen`, `uart_set_dtr_rts`
- **AI TX 화면 표시**: 수신 스트림에 섞지 않고 버퍼의 **메타 라인 타입**으로 삽입 (예: 회색 배경 `[AI→] ...`)
- **접근 제어**: MCP 활성/비활성 토글 + AI 읽기 전용(TX 차단) 모드 + 상태바 인디케이터
- 포트 분리 상태를 에러 모델로 노출 (AI가 재시도 판단 가능하게)

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

- 설정 저장/로드 다이얼로그 (보레이트 등은 고정 기본값 — 최소 상태 파일은 §6 Q3)
- 로깅 기능, 다중 실행 공식 지원 (중복 포트 에러 처리만 함), 명령줄 인자
- 풀 VT100 에뮬레이션 / 풀스크린 TUI (vim, menuconfig)
- 256색/트루컬러 (16색+bright로 시작, 필요 시 확장)
- 파일 전송(XMODEM류/Send file), 매크로, telnet/ssh, 검색, 화면 타임스탬프
- 크로스플랫폼, 다국어, 자동 업데이트

축소 전 전체 검토(설정/로깅/다중실행 포함 5개 관점 분석)는 git 이력의 이전 README 버전 참조.

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
