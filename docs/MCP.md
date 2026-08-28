# UartTerminal MCP 서버

UartTerminal은 **내장 MCP(Model Context Protocol) 서버**를 품고 있어, 사용자가 터미널로 UART 포트를 쓰는 **동시에** AI(Claude Code)가 같은 포트를 읽고 쓸 수 있다. AI가 보낸 데이터는 터미널 화면에 `[AI→]` 메타 라인으로 표시되어 사용자 입력과 시각적으로 구분된다.

이 문서는 MCP 서버의 구조, 등록 방법, 접근 제어, 그리고 제공하는 9개 도구의 사용법을 다룬다.

---

## 1. 왜 이런 구조인가 (in-process + Named Pipe + 릴레이)

Windows에서 **COM 포트는 한 프로세스만 열 수 있다**. 따라서 포트를 실제로 여는 MCP 서버는 반드시 UartTerminal(WPF) 프로세스 **안(in-process)** 에 있어야 한다. 별도 프로세스로 MCP 서버를 띄우면 포트를 두 번 열 수 없어 충돌한다.

한편 Claude Code는 MCP 서버를 **stdio**(표준 입출력)로 실행한다. 이 둘을 잇기 위해:

```
┌────────────┐   stdio    ┌───────────────────────┐  Named Pipe  ┌──────────────────────────────┐
│ Claude Code │──────────▶│ UartTerminal.McpRelay │─────────────▶│ UartTerminal.exe (WPF)       │
│  (MCP 클라)  │◀──────────│  (초소형 바이트 펌프)  │◀─────────────│  └ McpPipeServer (in-process) │
└────────────┘            └───────────────────────┘  uartterm-   │     └ UartBridge → SerialPort │
                                                       mcp-COM4    └──────────────────────────────┘
```

- **릴레이 exe** (`UartTerminal.McpRelay.exe COM4`): stdin/stdout ↔ 포트별 Named Pipe(`uartterm-mcp-COM4`) 사이를 그대로 통과시키는 순수 JSON-RPC 바이트 펌프. 상태를 갖지 않는다.
- **Named Pipe**: 포트마다 하나(`uartterm-mcp-<PORT>`). ACL은 **현재 사용자 전용**. 동적 포트/토큰/방화벽/Kestrel 문제가 전부 사라진다.
- **McpPipeServer** (in-process): 파이프 위에서 `StreamServerTransport`로 MCP 서버를 운영. 최대 4개 클라이언트 인스턴스 동시 수용.
- **UartBridge**: MCP 도구와 시리얼 세션·터미널 엔진·수신 링버퍼 사이의 **스레드 안전 파사드**. 모든 도구 호출이 여기로 위임된다.

> 포트별로 파이프가 분리되므로, 여러 탭(여러 COM 포트)을 열면 각 포트마다 독립된 MCP 서버가 뜬다. AI 등록도 포트별로 한다.

### 1.1 HTTP 엔드포인트 (병행)

위 구조에서 **"서버는 앱 안에"** 는 지금도 유효하다 — COM 포트를 한 프로세스만 열 수 있다는 사실은 전송 방식과 무관하다.
바뀔 수 있는 것은 **바깥으로 나가는 통로**뿐이고, Claude Code 가 HTTP 트랜스포트를 받으면서 릴레이가 필요 없어졌다.

```
┌────────────┐   HTTP    ┌──────────────────────────────────┐
│ Claude Code │─────────▶│ UartTerminal.exe (127.0.0.1:5300) │──▶ COM4 / COM9 / …
└────────────┘           │  └ McpHttpServer → BridgeRegistry │
                          └──────────────────────────────────┘
```

엔드포인트가 **앱당 하나**라 등록도 하나다. 어느 포트로 갈지는 도구의 `port` 인자가 정한다(§2.4).
설계 배경과 남은 단계는 [mcp-http-transport.md](mcp-http-transport.md) 에 있다.

---

## 2. 등록 및 활성화

### 2.1 MCP 서버 켜기 (GUI)

1. UartTerminal에서 대상 포트로 연결한다.
2. 메뉴 **[MCP] → "MCP 서버 활성화"** 체크.
   상태바에 `MCP: 켜짐 (uartterm-mcp-COM4) · HTTP 5300` 이 뜬다 — 앞쪽이 이 탭의 파이프, 뒤쪽이 앱 공용 HTTP 엔드포인트다.
3. 필요 시 **[MCP] → "AI 읽기 전용"** 으로 쓰기·제어를 차단할 수 있다(아래 §3).

### 2.2 Claude Code에 등록 — 두 가지 길

현재 **두 전송 방식이 나란히 돌아간다**(전환 1단계). 같은 브리지를 보므로 어느 쪽으로 붙어도 동작은 같다.

#### (가) HTTP — 권장

메뉴 **[MCP] → "HTTP 등록 명령 복사 (모든 포트)"**:

```bash
claude mcp add --transport http uart http://127.0.0.1:5300/mcp
```

- **PC 가 바뀌어도 같은 명령이다** — 체크아웃 경로도 COM 번호도 들어가지 않아 저장소의 `.mcp.json` 에 넣어도 된다.
- **포트를 몇 개 열든 등록은 하나.** 대신 도구를 부를 때 `port` 로 대상을 고른다(§2.4).
- 소켓은 `127.0.0.1` 에만 열리고, **MCP 를 켠 포트가 하나라도 있을 때만** 열린다.
- 포트 번호는 `%APPDATA%\UartTerminal\state.json` 의 `mcpHttpPort` 로 바꿀 수 있다(기본 5300).

#### (나) Named Pipe — 기존 방식

메뉴 **[MCP] → "파이프 등록 명령 복사 (이 포트만)"**:

```bash
claude mcp add uart-com4 -- "C:\path\to\UartTerminal.McpRelay.exe" COM4
```

> **포트가 바뀌면 재등록 필요**: 재연결 시 다른 COM 번호가 잡히면 파이프 이름도 바뀐다. 이때 상태바가 "MCP 재등록 필요"를 안내하며, 다시 "등록 명령 복사"로 새 명령을 받아 등록한다.
> HTTP 쪽은 URL 에 포트명이 없어 이 재등록이 필요 없다.

### 2.3 연결 조건

**파이프**: 릴레이가 붙으려면(최대 10초 대기) 다음이 모두 참이어야 한다 — UartTerminal 실행 중 · 해당 포트 탭 존재 · 그 탭에서 **MCP 서버 활성화**가 켜짐. 하나라도 아니면 릴레이는 "연결하지 못했습니다" 오류를 내고 종료한다.

**HTTP**: 조건은 하나다 — **MCP 를 켠 포트가 하나 이상**. 그때 소켓이 열리고, 마지막 하나를 끄면 닫힌다. 어느 탭인지는 상관없다.

> `UartTerminal.exe` 가 이미 떠 있는데 또 띄우면, 두 번째는 포트가 잡혀 있어 HTTP 를 포기하고 상태 표시줄에
> `HTTP 5300 사용 중` 을 띄운다. 앱은 정상 동작하며 그 창의 포트는 파이프로 쓸 수 있다.
> 먼저 뜬 인스턴스가 엔드포인트를 계속 쥔다.

### 2.4 여러 포트를 동시에 — `port` 인자

도구 9종 모두 **마지막 선택 인자 `port`** 를 받는다. 규칙은 이렇다:

| 상황 | 동작 |
|---|---|
| `port` 를 명시 | 그 포트. 노출돼 있지 않으면 오류 + 노출 목록 |
| 생략 + 노출된 포트 **1개** | 그 포트 (기존 프롬프트가 그대로 동작) |
| 생략 + 노출된 포트 **2개 이상** | **거절.** 목록을 돌려주고 지정하라고 요구 — 아무 데도 전송하지 않는다 |

마지막 줄이 중요하다. 마스터·슬레이브를 동시에 물린 상태에서 추측으로 하나를 고르면
엉뚱한 보드에 `uart_send "dfu erase"` 가 나갈 수 있다. 애매하면 거절하는 쪽이 맞다.

먼저 `uart_status` 를 `port` 없이 불러 무엇이 열려 있는지 보고, 이후 호출에 `port` 를 실어 보내면 된다.
(파이프로 붙은 경우에는 파이프 이름이 이미 포트를 지정하므로 `port` 를 생략해도 된다.)

---

## 3. 접근 제어

| 상태 | 읽기 계열<br>(`uart_status`/`uart_read`/`uart_expect`/`uart_screen`) | 쓰기·제어 계열<br>(`uart_send`/`uart_set_dtr_rts`/`uart_reset`/`uart_close`/`uart_open`) |
|---|---|---|
| **비활성** (MCP 꺼짐) | 호출 불가 — 파이프는 닫혀 있고, HTTP 는 그 포트가 노출 표에서 빠진다 | 불가 |
| **활성 + 읽기 전용** | 허용 | 차단 → `error: "read_only"` |
| **활성 + 일반** | 허용 | 허용 |

- 읽기 전용 모드는 AI가 **관찰만** 하고 장치·세션에 영향을 주지 못하게 한다. 펌웨어 플래싱(`uart_close`/`uart_open`)도 제어 행위이므로 읽기 전용에서 차단된다.
- 포트가 닫혀 있으면(미연결/양보 상태) 쓰기·상태선 도구는 `error: "disconnected"` 를 반환한다.

---

## 4. 도구 레퍼런스 (9종)

모든 반환값은 snake_case JSON이다. 시간이 걸리는 값은 `error` 필드로 사유를 명시한다.

> 아래 표에는 적지 않았지만 **9종 모두 마지막에 선택 인자 `port`** 를 받는다(§2.4).
> 생략 규칙 때문에 포트를 하나만 열었을 때는 지금까지 쓰던 호출이 그대로 동작한다.

### 4.1 `uart_status` (읽기)
현재 연결 상태와 수신 버퍼 커서 정보를 반환한다.

| 반환 필드 | 의미 |
|---|---|
| `port`, `connected`, `mcp_enabled`, `read_only` | 포트명 / 연결 여부 / MCP 활성 / 읽기전용 |
| `baud`, `line` | 보드레이트 / 회선 설정 요약(예: `115200 8N1`) |
| `dtr`, `rts` | 현재 제어선 상태 |
| `total_received_bytes` | 누적 수신 바이트(= `end_cursor`) |
| `retained_bytes`, `oldest_cursor` | 링버퍼에 남은 바이트 / 가장 오래된 커서 |
| `end_cursor` | 다음에 이어 읽을 커서(기억해 두었다가 `uart_read`/`uart_expect`에 넘김) |

### 4.2 `uart_send` (쓰기, 읽기전용 시 차단)
UART로 텍스트를 전송한다. 사용자 입력과 **동일한 단일 TX 큐**로 원자적으로 나간다. 화면에는 `[AI→]` 로 표시.

| 인자 | 기본 | 의미 |
|---|---|---|
| `text` | (필수) | 전송할 텍스트. 내부 개행은 **송신 개행 설정값**(기본 CR)으로 정규화 — 사용자 입력과 같은 규약 |
| `append_newline` | `true` | 끝에 개행을 붙임(esp_console 명령처럼 실행) |

반환: `ok`, `bytes_sent`, `error?`

### 4.3 `uart_read` (읽기)
수신 버퍼를 커서 기준으로 읽는다.

| 인자 | 기본 | 의미 |
|---|---|---|
| `cursor` | (생략=가장 오래된 위치) | 읽기 시작 절대 오프셋 |
| `max_bytes` | `8192` | 한 번에 읽을 최대 바이트 |
| `strip_ansi` | `true` | ANSI 이스케이프/제어 문자 제거 |

반환: `data`, `cursor`(다음 호출용), `dropped_bytes`(용량 초과 유실), `end_cursor`, `more`, `connected`

### 4.4 `uart_expect` (읽기)
정규식/리터럴 패턴이 수신 스트림에 나타날 때까지 대기(폴링 왕복 최소화). `uart_send` 직후 응답 대기에 적합.

| 인자 | 기본 | 의미 |
|---|---|---|
| `pattern` | (필수) | 찾을 패턴 |
| `timeout_ms` | `5000` | 대기 시간 |
| `cursor` | (생략=지금 이후) | 탐색 시작 커서 |
| `strip_ansi` | `true` | ANSI 제거 |
| `regex` | `true` | true=.NET 정규식, false=리터럴 |
| `match_mode` | `"stream"` | `stream`=도착하는 대로 평가 / `line`=완성된 줄까지만 평가 |

반환: `matched`, `timed_out`, `match`, `groups`, `data`, `cursor`, `dropped_bytes`, `error?`(`bad_pattern`/`regex_timeout`/`canceled`)

#### 조기 매칭 함정과 `match_mode`

수신은 스트림이므로 기본(`stream`) 모드는 **바이트가 도착하는 대로** 패턴을 평가한다. 따라서 열린 수량자로 끝나는 패턴은 값이 다 도착하기 전에 걸린다:

```
장비 출력:  mode : CDC\r\n      (실제로는 "mode : C" → "DC\r\n" 두 조각으로 도착)
패턴:       mode\s+:\s+\w+
결과:       match = "mode : C"   ← 조기 매칭
```

해결책 셋 중 하나를 쓴다:

| 방법 | 예 | 비고 |
|---|---|---|
| **완결 패턴** | `mode\s+:\s+(CDC\|MSC)` | 후보를 열거 — 가장 견고 |
| **개행 포함** | `mode\s+:\s+\w+\r?\n` | 줄이 끝나야 성립 |
| **`match_mode="line"`** | 패턴 그대로 | 완성된 줄까지만 평가 |

`line` 모드의 대가: **개행이 오지 않는 프롬프트**(`xcp> `)는 줄이 완성되지 않아 매칭되지 않는다. 프롬프트를 기다릴 때는 `stream`(기본)을 쓴다. 그래서 `line`은 기본값이 아니라 옵션이다.

> 참고: 청크 경계에 걸린 ANSI 이스케이프(`ESC[3` | `2m`)는 `strip_ansi` 시 커서를 완전한 시퀀스 경계까지만 전진시켜 보류한다(UTF-8 멀티바이트 경계와 같은 방식). 따라서 뒤 조각이 리터럴 텍스트(`2m`)로 새어 오탐 매칭을 만들지 않는다.

### 4.5 `uart_screen` (읽기)
현재 터미널 화면(논리 라인 버퍼)의 최근 내용을 **사람이 보는 형태**로 스냅샷. CR 덮어쓰기/커서 편집이 반영되어 프롬프트·표 상태 파악에 적합. raw 바이트가 필요하면 `uart_read`.

| 인자 | 기본 | 의미 |
|---|---|---|
| `max_lines` | `50` (최대 2000) | 반환할 최근 논리 라인 수 |

반환: `text`, `line_count`, `total_lines`

### 4.6 `uart_set_dtr_rts` (제어, 읽기전용 시 차단)
DTR/RTS 제어선을 설정. ESP32 리셋/부트로더 진입 시퀀스 등에 사용(**보드가 리셋될 수 있음**).

| 인자 | 의미 |
|---|---|
| `dtr`, `rts` | 각 제어선 상태 |

반환: `ok`, `dtr`, `rts`, `error?`

### 4.7 `uart_reset` (제어, 읽기전용 시 차단) — 신규
ESP32 devkit 의 자동 프로그램 회로(DTR→IO0 / RTS→EN)로 보드를 리셋한다. `uart_set_dtr_rts` 를 여러 번 호출하는 것과 달리 **대기 시간(100ms/50ms)이 보장**돼 왕복 없이 한 번에 끝난다.

| 인자 | 기본 | 의미 |
|---|---|---|
| `bootloader` | `false` | `false`=일반 리셋(부팅 로그를 처음부터 관측), `true`=IO0=LOW 로 다운로드(플래싱) 모드 진입 |

반환: `ok`, `mode`(`hard` | `bootloader`), `dtr`, `rts`, `error?`

화면에는 `[AI→] 하드웨어 리셋(EN 펄스)` 메타 라인이 남는다. 시퀀스 상세와 극성(assert=LOW)은 [README §2.2](../README.md#22-보드-리셋--부트로더-진입-esp32-devkit).

### 4.8 `uart_close` (제어, 읽기전용 시 차단) — 신규
터미널이 점유한 포트를 **닫아 양보**한다. `esptool` 같은 외부 도구가 포트를 독점해야 할 때 호출. 반환 후 포트가 해제된다. 닫혀 있는 동안 **자동 재연결(USB 재접속 감시)은 일시 중지**된다.

반환: `ok`, `connected`(false), `port`, `state`(`closed` | `already_closed`), `error?`

### 4.9 `uart_open` (제어, 읽기전용 시 차단) — 신규
`uart_close`로 양보했거나 끊긴 포트를 **같은 포트명·같은 설정**으로 다시 연다. 외부 작업 종료 후 호출.

반환: `ok`, `connected`, `port`, `state`(`open` | `already_open` | `in_use` | `error`), `error?`

> 외부 도구가 아직 포트를 쥐고 있으면 `ok:false, state:"in_use", error:"in_use"` — 수백 ms 후 재시도.

---

## 5. 커서 모델 (읽기 흐름)

수신 버퍼는 **단조 증가하는 절대 바이트 오프셋**(커서)으로 읽는다. 1 MiB 링버퍼를 넘겨 유실된 구간은 `dropped_bytes`로 명시된다.

```
1) uart_status → end_cursor 기억 (예: 10240)
2) uart_send("help", append_newline=true)
3) uart_expect(pattern="esp32>", cursor=10240, timeout_ms=3000)
   → matched:true, data:"...", cursor:10310
4) 이어 읽기: uart_read(cursor=10310)  → 다음 데이터만
```

- `cursor`를 **생략**하면: `uart_read`는 보관된 **가장 오래된** 위치부터(백로그), `uart_expect`는 **지금 이후** 도착분을 기다린다.
- 반환된 `cursor`를 다음 호출에 넘겨 **중복 없이 이어서** 읽는다.
- `dropped_bytes > 0`이면 버퍼 용량 초과로 유실이 있었다는 뜻.

---

## 6. ESP32 펌웨어 플래싱 워크플로우 (포트 양보)

`esptool`은 ROM 부트로더 핸드셰이크·SLIP 프레이밍 등 **바이너리 프로토콜**로 포트를 **독점**한다. `uart_send`(평문)로는 대체할 수 없고, 터미널이 포트를 쥐고 있으면 `esptool`이 포트를 열 수 없다.

`uart_close` / `uart_open`으로 포트를 잠시 양보했다가 되찾는다:

```
1) uart_close
     → { ok:true, state:"closed" }         # 터미널이 포트 해제, 자동 재연결 중지
        (터미널 탭 제목: "COM4 [AI 양보]", 상태 점: 보라색)

2) (Claude Code 셸에서 esptool 실행)
     esptool --port COM4 --baud 921600 write_flash 0x10000 app.bin

3) uart_open
     → { ok:true, state:"open" }            # 재연결 완료, 부팅 로그 다시 수신
     → 만약 { ok:false, state:"in_use" }    # esptool이 아직 포트 사용 중 → 잠시 후 재시도

4) uart_expect(pattern="app_main", timeout_ms=10000)   # 부팅 확인
```

- **외장 USB-UART(CP2102/CH340 등)**: 포트가 그대로 있어 위 흐름이 그대로 동작.
- **ESP32-S3/C3 내장 USB(native CDC)**: 리셋 시 포트가 잠깐 사라졌다 다시 뜨는데, `esptool`이 처리하고 이후 `uart_open`으로 복귀한다. `in_use`/`error`가 나오면 재시도.

> **주의**: `uart_close`는 사용자의 라이브 세션을 끊는 제어 행위다. 읽기 전용 모드에서는 차단된다. AI가 명시적으로 닫은 포트는 자동 재연결이 개입하지 않으므로, 작업이 끝나면 **반드시 `uart_open`으로 되돌려야** 한다(또는 사용자가 [터미널]>재연결).

---

## 7. 오류 모델

| `error` 값 | 의미 | AI 대응 |
|---|---|---|
| `mcp_disabled` | MCP 서버 비활성 | 사용자에게 활성화 요청 |
| `read_only` | 읽기 전용 모드 | 사용자에게 해제 요청(의도된 차단) |
| `disconnected` | 포트가 열려 있지 않음 | `uart_open` 또는 사용자 재연결 대기 |
| `in_use` | 포트가 다른 프로그램 점유 중 | 수백 ms 후 재시도 |
| `not_supported` | 포트 제어 핸들러 미등록(비정상) | 재시도/보고 |
| `bad_pattern`, `regex_timeout` | expect 패턴 문제 | 패턴 수정 |
| `exception: …` | 내부 예외(디스패처 종료 경합 등) | 재시도/보고 |

포트 분리·양보 상태는 모두 **에러 모델로 노출**되어 AI가 재시도 여부를 스스로 판단할 수 있다.

---

## 8. 문제 해결

- **릴레이가 연결 실패**: UartTerminal 실행 여부, 해당 포트 탭 존재, `[MCP] 서버 활성화` 체크 확인.
- **도구가 `read_only` 반환**: `[MCP] → AI 읽기 전용` 해제.
- **포트 변경 후 도구 미동작**: 파이프 이름이 바뀌었다 → "등록 명령 복사"로 재등록.
- **`uart_open`이 계속 `in_use`**: 외부 도구(esptool 등)가 아직 포트를 잡고 있음 → 프로세스 종료 확인 후 재시도.
- **여러 창/탭**: 파이프는 포트별로 독립이라 등록도 포트별이다. HTTP 는 등록 하나로 전부 보고 `port` 로 고른다.
- **HTTP 로 부르는데 "열린 포트가 여러 개입니다" 오류**: 의도된 거절이다 → `port` 를 지정하거나 `uart_status` 로 목록을 먼저 본다.
- **HTTP 가 안 붙음**: 상태 표시줄의 `HTTP …` 표시를 본다 — 아무 표시도 없으면 MCP 를 켠 포트가 없는 것이고, `HTTP 5300 사용 중` 이면 다른 인스턴스가 쥐고 있는 것이다.

---

## 관련 코드

| 파일 | 역할 |
|---|---|
| `src/UartTerminal/Mcp/UartMcpTools.cs` | 9개 MCP 도구 정의(속성/설명) |
| `src/UartTerminal/Mcp/UartBridge.cs` | 스레드 안전 파사드(세션·링버퍼·접근제어·포트 제어 위임) |
| `src/UartTerminal/Mcp/McpPipeServer.cs` | 포트별 Named Pipe 위의 in-process MCP 서버 |
| `src/UartTerminal/Mcp/McpHttpServer.cs` | 앱당 하나인 HTTP(Streamable) 엔드포인트 — 127.0.0.1 고정, Origin 검사 |
| `src/UartTerminal/Mcp/BridgeRegistry.cs` | `포트명 → UartBridge` 표와 `port` 해석 규칙(등록 = 노출) |
| `src/UartTerminal/Mcp/McpHost.cs` | 앱 수준 배선 — 노출된 포트가 있을 때만 소켓을 연다 |
| `src/UartTerminal.McpRelay/Program.cs` | stdio ↔ Named Pipe 릴레이 exe |
| `src/UartTerminal/UartDocumentView.xaml.cs` | 세션 수명주기 소유(`McpReleasePort`/`McpReopenPort` 등) |
