# MCP 트랜스포트를 HTTP로 전환 — 설계 메모

> 현재의 **stdio + 릴레이 + Named Pipe** 구조를 **HTTP 엔드포인트 하나**로 바꾸는 검토안.
> 구현 전 결정 사항을 정리한 메모다. 아직 착수하지 않았다.

- 대조 기준: `src/UartTerminal/Mcp/` (`McpPipeServer.cs` 196줄, `UartMcpTools.cs` 112줄, `UartBridge.cs` 543줄), `src/UartTerminal.McpRelay/Program.cs` 86줄
- 현재 SDK: `ModelContextProtocol.Core` 1.4.1
- 관련 문서: [MCP.md](MCP.md)

---

## 0. 요약

**바꿀 가치가 있다.** 다만 진짜 이득은 트랜스포트 교체 자체가 아니라, 그에 딸려 오는 **구조 변경**에서 나온다.

| | 지금 | 전환 후 |
|---|---|---|
| MCP 서버 인스턴스 | **COM 포트마다 1개** (문서 탭마다 생성) | **앱당 1개** |
| Claude 등록 | 포트마다 1개씩 | **전체 1개** |
| 등록 명령에 들어가는 값 | 실행 파일 절대 경로 + COM 번호 | **URL 하나** |
| 별도 프로세스 | `McpRelay.exe` | 없음 |

핵심 결정은 **"도구에 `port` 파라미터를 추가한다"** 이다. 이게 없으면 HTTP로 바꿔도 포트마다 엔드포인트가 하나씩 필요해서 얻는 게 없다.

---

## 1. 지금 구조와 그 대가

```
Claude ──stdio──> McpRelay.exe COM4 ──Named Pipe──> UartTerminal.exe ──> COM4
        (Claude가 띄움)              uartterm-mcp-COM4   (사용자가 띄움)
```

`McpPipeServer` 는 **문서 뷰(열린 포트 탭)마다 생성**된다.

```
src/UartTerminal/UartDocumentView.xaml.cs:280   _mcpServer = new McpPipeServer(_bridge, _portName);
src/UartTerminal/UartDocumentView.xaml.cs:372   _mcpServer = new McpPipeServer(_bridge, _portName);
```

파이프 이름이 `uartterm-mcp-{PORT}` 라서, 포트별로 서버·파이프·릴레이·등록이 **전부 1:1로 늘어난다.**

### 이 구조가 치르는 비용 3가지

**① 등록 명령에 PC마다 다른 값이 두 개 들어간다**

```bash
claude mcp add uart-com4 -- "C:\work\Tools\UartTerminal\src\UartTerminal.McpRelay\bin\Debug\net10.0-windows\UartTerminal.McpRelay.exe" COM4
                             └─────────────── 절대 경로 ───────────────┘                                                        └ COM 번호 ┘
```

- **절대 경로** — PC마다 체크아웃 위치가 다르다
- **COM 번호** — PC마다, USB 포트를 바꿔 꽂아도 달라진다

그래서 저장소의 `.mcp.json`(git 커밋됨)에 넣을 수 없고, **PC를 옮길 때마다 다시 등록**해야 한다.

**② 보드 2대를 보려면 등록도 2개**

마스터·슬레이브를 동시에 물리면 `uart-com7`, `uart-com9` 처럼 등록이 늘어난다. 포트가 재열거되면 둘 다 다시 등록해야 한다.

**③ 릴레이라는 계층이 하나 더 있다**

`McpRelay` 는 stdin/stdout ↔ Named Pipe 를 잇는 순수 바이트 펌프다. 로직이 없다. **Claude Code가 MCP 서버를 stdio로만 띄운다는 제약** 때문에 존재하는 어댑터다. HTTP를 쓰면 그 제약이 사라진다.

---

## 2. 전환 후

```
Claude ──HTTP──> UartTerminal.exe (127.0.0.1:5300) ──> COM4 / COM7 / COM9 …
```

```bash
claude mcp add --transport http uart http://127.0.0.1:5300
```

- **PC가 바뀌어도 같은 명령**
- 저장소 `.mcp.json` 에 커밋해도 안전
- 포트를 몇 개 열든 **등록은 하나**
- 릴레이 프로젝트 삭제
- 파이썬 스크립트 등 다른 클라이언트도 같은 엔드포인트를 공유

---

## 3. 설계

### 3.1 엔드포인트는 앱당 하나

`McpPipeServer` 를 문서 뷰에서 생성하던 것을 **앱 수준 단일 호스트**로 옮긴다.

```
[지금]  UartDocumentView (COM4)  → McpPipeServer(bridge_COM4, "COM4")
        UartDocumentView (COM7)  → McpPipeServer(bridge_COM7, "COM7")

[전환]  App                      → McpHttpServer(포트 5300)
                                     └ 열린 브리지 목록을 참조
        UartDocumentView (COM4)  → 브리지를 레지스트리에 등록/해제
        UartDocumentView (COM7)  → 〃
```

**브리지 레지스트리**(`포트명 → UartBridge`)가 새로 필요하다. 문서 뷰는 열릴 때 등록하고 닫힐 때 해제한다.

### 3.2 ★ 도구에 `port` 파라미터를 추가한다

현재 9종 도구는 포트 인자가 없다. 인스턴스가 이미 한 포트에 묶여 있기 때문이다.

```csharp
public SendResult Send(string text, bool append_newline = true) => _bridge.Send(text, append_newline);
```

엔드포인트가 하나가 되면 **어느 포트인지 지정할 수단**이 필요하다.

```csharp
public SendResult Send(string text, bool append_newline = true, string? port = null)
    => Resolve(port).Send(text, append_newline);
```

`CanTerminal` 의 `can_send(channel: "CAN1")` 과 같은 모양이다.

### 3.3 ★ `port` 생략 규칙 — 틀린 보드로 나가지 않게

`Resolve(port)` 규칙을 이렇게 둔다.

| 상황 | 동작 |
|---|---|
| `port` 를 명시 | 그 포트. 없으면 오류 + 열린 포트 목록 반환 |
| 생략 + 열린 포트 **1개** | 그 포트 (기존 사용법 그대로 동작) |
| 생략 + 열린 포트 **2개 이상** | **오류.** 열린 포트 목록을 돌려주고 명시하라고 요구 |

> 마지막 줄이 중요하다. **추측해서 하나를 고르면 안 된다.**
> 마스터·슬레이브를 동시에 물린 상태에서 엉뚱한 보드에 `uart_send "dfu erase"` 가 나가면 곤란하다.
> 애매하면 거절하고 목록을 보여주는 쪽이 맞다.

`uart_status` 는 반대로 **포트 생략 시 전체 목록**을 반환하도록 확장한다. AI가 먼저 `uart_status` 로 무엇이 열려 있는지 보고, 이후 호출에 `port` 를 실어 보내는 흐름이 된다.

### 3.4 기존 사용법 호환

`port` 를 **마지막 선택 파라미터**로 붙이고 기본값을 `null` 로 두면, 포트가 하나만 열린 흔한 경우에는 기존 프롬프트가 그대로 동작한다. 기존 ESP32 펌웨어 쪽 사용법을 깨지 않는다.

---

## 4. 구현

### 4.1 패키지 — 스파이크로 확인함 (2026-08-28)

**결론: 된다.** `ModelContextProtocol.AspNetCore` **1.4.1** (Core 와 버전이 1:1). 별도 프로젝트에서
실제로 빌드·기동·HTTP 왕복까지 확인했고, 다른 에이전트가 새 폴더에서 독립 재현했다.

```xml
<PackageReference Include="ModelContextProtocol.Core" Version="1.4.1" />
<PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.4.1" />
```

- **`FrameworkReference` 는 쓸 필요 없다.** 이 패키지의 nuspec 이 `Microsoft.AspNetCore.App` 을
  직접 선언해서 `runtimeconfig.json` 에 전이로 올라온다(명시해도 결과 파일이 바이트 단위로 동일 — no-op).
- **Web SDK 로 바꿀 필요도 없다.** `Microsoft.NET.Sdk` + `UseWPF=true` + `OutputType=WinExe` 그대로
  Kestrel 이 뜨고, 한 프로세스에 `PresentationFramework.dll` 과 Kestrel 이 함께 로드된다.
- 의존성이 하나 늘어난다: AspNetCore → **ModelContextProtocol**(호스팅/DI) → Core.
  `AddMcpServer()`/`WithTools<T>()` 는 Core 가 아니라 가운데 어셈블리에 있다.

**실측으로 걸러낸 함정 (전부 코드에 반영됨)**

| 함정 | 실제로 일어난 일 | 대응 |
|---|---|---|
| `RunAsync()` 파이어앤포겟 | 바인드 실패가 Task 안에 갇히고, 다음에 `app.Services` 를 건드리는 순간 **원인과 무관한 `ObjectDisposedException`** 이 튄다 | `StartAsync().GetAwaiter().GetResult()` — 진짜 예외를 그 자리에서 던진다 |
| 툴이 도는 스레드 | 툴은 **스레드풀 MTA** 에서 불린다(UI 스레드 아님) | 기존 `SetPortController` 의 Dispatcher 마샬링을 그대로 유지 |
| 엔드포인트 경로 | `MapMcp()` 기본값은 루트라 `/mcp` 가 404, 반대로 하면 `/` 가 404 | `/mcp` 로 고정 |
| 콘텐트 루트 | 현재 작업 디렉터리를 따라간다(바로가기로 띄우면 `C:\Windows`) | `ContentRootPath = AppContext.BaseDirectory` |
| 포트 충돌 예외 | `IOException` → inner `AddressInUseException`. **inner 메시지는 OS 로케일로 번역된다** | 문자열이 아니라 **타입**으로 잡는다 |
| WinExe 에 콘솔 없음 | 기동 중 예외가 조용히 사라진다 | `Logging.ClearProviders()` + 실패를 `DiagLog`·상태 표시줄로 |

`HttpListener` 로 직접 구현하는 길도 있으나, Streamable HTTP 의 세션/스트리밍 규약을 손으로 짜야 해서 택하지 않았다.

### 4.1.1 ★ 배포 크기 — 메모가 놓쳤던 비용

`bin` 폴더만 보면 **+40 KB** 라 공짜처럼 보인다. 착시다.

| | 단일파일 FD | 단일파일 self-contained |
|---|---|---|
| 전 | 3.25 MB | **128.5 MB** |
| 후 | 3.33 MB | **154.7 MB** |
| 차이 | +80 KB | **+26.2 MB (+20.4%)** |

`.github/workflows/build.yml` 이 self-contained single-file 을 배포하므로 **릴리스 산출물이 +26 MB** 다.
framework-dependent 쪽은 크기는 그대로지만 **ASP.NET Core 런타임을 새로 요구한다** — WPF 사용자가 흔히 깔아 두는
.NET Desktop Runtime 에는 없고, 없으면 관리 코드가 돌기 전에 호스트가 죽어 `try/catch` 로도 못 잡는다.

> 이 비용은 3단계(파이프 삭제)까지 갔을 때 **트랜스포트 하나에 26 MB** 를 낸다는 뜻이다.
> 1단계는 되돌리기 쉬우니, 2단계로 넘어가기 전에 한 번 판단할 값어치가 있다.

### 4.2 바뀌는 파일

| 파일 | 작업 |
|---|---|
| `Mcp/McpHttpServer.cs` | **신규** — 앱 수준 호스트, `127.0.0.1` 바인딩 |
| `Mcp/BridgeRegistry.cs` | **신규** — `포트명 → UartBridge` |
| `Mcp/UartMcpTools.cs` | 9종에 `port` 파라미터 추가, `Resolve()` 도입 |
| `UartDocumentView.xaml.cs:280,372` | `McpPipeServer` 생성 → **레지스트리 등록/해제**로 교체 |
| `App.xaml.cs` (또는 메인 윈도우) | 호스트 기동/종료 |
| `Mcp/McpPipeServer.cs` | 3단계에서 삭제 |
| `src/UartTerminal.McpRelay/` | 3단계에서 프로젝트째 삭제 |

`UartBridge.cs`(543줄)는 **손대지 않는다.** 이미 스레드 안전하고 트랜스포트와 무관하다.

### 4.3 단계

```
1단계  HTTP 서버를 추가하되 Named Pipe 도 그대로 둔다 (병행)   ← 완료 (2026-08-28)
       → 두 경로가 같은 브리지를 본다. 기존 등록이 안 깨진다.
2단계  Claude 등록을 HTTP 로 갈아탄다. 실사용으로 검증.
3단계  McpPipeServer + McpRelay 프로젝트 삭제. README/MCP.md 갱신.
```

1단계에서 멈춰도 손해가 없다. 되돌리기 쉽다 — 단 §4.1.1 의 배포 크기는 이미 든다.

### 4.4 1단계에서 실제로 들어간 것

| 파일 | 한 일 |
|---|---|
| `Mcp/BridgeRegistry.cs` | **신규** — `포트명 → UartBridge` + §3.3 해석 규칙. 파이프용 `SingleBridgeResolver` 도 여기 |
| `Mcp/McpHttpServer.cs` | **신규** — Kestrel 호스트(127.0.0.1 고정), Origin 검사, 포트 충돌 처리 |
| `Mcp/McpHost.cs` | **신규** — 앱 수준 배선. 노출 수가 0↔1 을 넘을 때만 소켓을 열고 닫는다 |
| `Mcp/UartMcpTools.cs` | 9종 전부에 마지막 선택 인자 `port` 추가, `Resolve()` 도입, HTTP용 안내문 |
| `Mcp/McpPipeServer.cs` | 툴을 `SingleBridgeResolver` 로 생성(한 줄). **그 밖에는 손대지 않음** |
| `UartDocumentView.xaml.cs` | MCP 를 켜고/끄고/포트가 바뀌고/탭이 닫힐 때 레지스트리 등록·해제 |
| `App.xaml.cs` | `McpHost.Init` / `OnExit` 에서 `Shutdown` |
| `AppState.cs` | `McpHttpPort` (기본 5300) |
| `ShellWindow.xaml(.cs)` | 상태 표시줄에 HTTP 상태, [MCP] 메뉴에 "HTTP 등록 명령 복사" |

`UartBridge.cs`(543줄)는 **한 줄도 고치지 않았다.** 읽기 전용·활성 게이트가 브리지 안에 있어서
`Resolve(port)` 가 브리지를 돌려주는 것만으로 판정이 따라온다.

**등록 = 노출.** 레지스트리에 넣고 빼는 것이 곧 접근 허용/차단이다 —
읽기 계열 4종(`uart_read`·`uart_status`·`uart_screen`·`uart_expect`)에는 브리지 안에 활성 검사가 없어서
(쓰기 계열만 `_enabled` 를 본다) 표에 남아 있으면 읽힌다. 파이프 시절에는 서버를 안 띄우면
접근 자체가 불가능해 문제가 없었지만, 엔드포인트를 공유하는 지금은 표에서 빼는 것이 유일한 차단이다.

---

## 5. 정한 것

> 2026-08-28 결정. 네 항목 모두 **가장 작은 변경** 쪽으로 정했다 —
> 이 전환의 목적은 등록 URL을 고정하는 것이지 앱 동작을 새로 만드는 게 아니다.

| 항목 | **정한 값** | 근거 |
|---|---|---|
| **기본 포트 번호** | **5300**, `state.json` 에서 변경 가능 | 흔한 개발 포트(3000·5000·5173·8080)와 안 겹친다. `AppState.McpHttpPort` 추가 — 앱 전역 설정이 이미 한 곳(`%APPDATA%\UartTerminal\state.json`)에 모여 있어 자리가 있다 |
| **인스턴스 2개** | **두 번째는 HTTP 를 포기** (거절) | 바인딩 실패를 잡아 HTTP 없이 정상 동작하고, 상태 표시줄로 알린다. 등록 URL 이 안 흔들린다. 1단계에서는 Named Pipe 가 살아 있어 **두 번째 인스턴스도 기능 손실이 없다.** 단일 인스턴스 앱으로 바꾸는 건 지금 없는 동작을 새로 만드는 것이라 이번 범위 밖 |
| **읽기 전용 모드** | **포트별 유지** | `UartBridge.ReadOnly` 를 그대로 쓴다. `Resolve(port)` 가 브리지를 돌려주므로 판정이 **자동으로 따라온다** — 추가 코드 사실상 0. 마스터는 읽기 전용, 슬레이브는 쓰기 허용 같은 구성도 유지된다 |
| **MCP 켜기 UI** | **노출된 포트가 1개 이상일 때만 기동** | 포트별 토글의 의미를 "이 포트를 MCP 에 노출"로 바꾸고, 노출이 0 이면 소켓을 열지 않는다. MCP 를 안 쓰는 날에는 지금과 체감이 같고, 세션의 `McpOnOpen` 도 그대로 산다 |

### 결정이 코드에 닿는 지점

| 결정 | 파일 |
|---|---|
| 포트 번호 | `AppState.cs` — `McpHttpPort` (기본 5300) |
| 두 번째 인스턴스 | `McpHttpServer.Start()` — 바인딩 예외를 삼키고 `Failed` 상태로. 상태 표시줄 메시지 |
| 읽기 전용 | **변경 없음** (`UartBridge.ReadOnly`) |
| 기동 시점 | `BridgeRegistry` 등록 수가 0↔1 을 넘을 때 호스트 기동/정지 |

### 바인딩은 반드시 `127.0.0.1`

`0.0.0.0` 에 열면 같은 네트워크의 누구나 **UART로 임의 명령을 쏠 수 있다.** 원격 접근이 필요해지면 그때 별도로 설계한다.

---

## 5.1 "데이터가 많이 쏟아지는데 HTTP 가 맞나"

**전제가 어긋나 있다 — MCP 트랜스포트는 UART 스트림을 나르지 않는다.**

```
SerialPort ──▶ ConnectionController ──▶ TerminalEngine (화면, 60Hz 렌더)
                                   └──▶ RxRingBuffer (1 MiB)
                                              ▲
                                              │ uart_read(cursor, max_bytes)  ← 부를 때만
                                          MCP 트랜스포트
```

수신 바이트는 링버퍼에 쌓일 뿐이고, MCP 는 **AI 가 호출할 때만** 커서로 꺼내 간다.
서버가 밀어내는 알림·스트리밍은 하나도 없다(코드 검색 0건). 즉 트랜스포트에 걸리는 부하는
**UART 속도가 아니라 AI 의 호출 빈도**에 비례한다. 대용량을 실제로 감당하는 곳은 화면 렌더 경로이고,
그쪽은 MCP 와 완전히 분리돼 있다(v1.12.0 에서 최적화한 GlyphRun 경로).

### 그래도 숫자로 확인함 (루프백 실측, 2026-08-28)

| 호출 | 처리량 | 지연 | 응답 본문 |
|---|---|---|---|
| `uart_status` | 2,813 회/초 | 0.36 ms | 227 B |
| `uart_read` 8 KiB(기본) | 1,477 회/초 · 11.9 MB/s | 0.68 ms | 8.3 KB |
| `uart_read` 64 KiB(최대) | 404 회/초 · **25.4 MB/s** | 2.47 ms | 65.6 KB |

| 기준 | 실효 속도 | 여유 |
|---|---|---|
| 115200 8N1 | 11.5 KB/s | **약 2,300배** |
| 921600 8N1 | 92.2 KB/s | **약 287배** |

1 MiB 링버퍼를 통째로 비우는 데 64 KiB × 16회 = **약 40 ms**.

### 진짜 병목은 트랜스포트가 아니다

파이프가 HTTP 보다 호출당 빠른 것은 맞지만(프레이밍·헤더가 없다), 절대값이 문제가 되는 영역이 아니다.
**정작 대용량에서 위험한 것은 링버퍼 유실**이고, 이건 전송 방식과 무관하다:

| 속도 | 1 MiB 링버퍼가 버티는 시간 |
|---|---|
| 115200 | 약 91초 |
| 921600 | 약 11초 |

AI 가 그보다 오래 안 읽으면 그 구간은 사라지고 `dropped_bytes` 로 보고된다.
대응은 트랜스포트 교체가 아니라 **`uart_expect` 로 폴링 왕복을 줄이거나 링버퍼를 키우는 것**이다.

> 덧붙여, 만약 나중에 "UART 이벤트를 AI 에게 밀어내기" 가 필요해지면 **HTTP 쪽이 오히려 유리하다** —
> SSE 가 이미 깔려 있다. 파이프로 같은 것을 하려면 릴레이까지 손봐야 한다.

HTTP 가 실제로 지불하는 비용은 처리량이 아니라 **배포 +26 MB(§4.1.1)** 와 **기동 203 ms** 다.

---

## 6. 안 바뀌는 것

- **`UartTerminal.exe` 는 여전히 사용자가 띄워야 한다.** COM 포트를 쥔 프로세스가 있어야 하는 건 트랜스포트와 무관하다.
- 도구 9종의 의미·커서 모델·ANSI 처리·읽기 전용 정책 — 그대로.
- `UartBridge` 내부 — 그대로.

---

## 7. 위험

| | 내용 | 완화 | 상태 |
|---|---|---|---|
| 포트 지정 실수 | 엉뚱한 보드에 명령이 나감 | §3.3 — 포트 2개 이상이면 **생략 금지** | 처리됨 (HTTP 왕복 테스트로 고정) |
| WPF + 웹 호스트 | UI 스레드/종료 처리, 패키지 호환 | 1단계 병행 + 사전 스파이크 | 처리됨 (§4.1) |
| 포트 충돌 | 다른 프로그램이 5300 사용 | `AddressInUseException` 을 타입으로 잡아 `PortInUse` 상태로, 앱은 파이프로 계속 동작 | 처리됨 |
| 방화벽 팝업 | 첫 실행 시 Windows 방화벽 | `127.0.0.1` 바인딩이면 대개 뜨지 않음. 뜨면 거부해도 동작 | — |
| **로컬 무인증** | 루프백에 열어도 **브라우저는 닿는다** — 사용자가 방문한 웹페이지가 `fetch('http://127.0.0.1:5300/mcp')` 로 UART 를 조작할 수 있다(DNS rebinding) | MCP 규약이 권고하는 **Origin 검사**. MCP 클라이언트는 Origin 을 안 붙이고 브라우저는 반드시 붙이므로, 로컬이 아닌 Origin 이면 403 | 처리됨 |
| **배포 +26 MB** | self-contained single-file 이 128 → 155 MB | 1단계는 되돌리기 쉬움. 2단계 전에 판단 | **미결 — §4.1.1** |
| 남은 구멍 | Origin 을 안 붙이는 **로컬 프로세스**는 여전히 접근 가능 | 파이프도 같은 성질이었다(현재 사용자 ACL). 토큰이 필요해지면 별도 설계 | 알고 있음 |

---

## 8. 검증

자동 검증은 `tests/UartTerminal.Tests/` 의 `BridgeRegistryTests`(규칙)와 `McpHttpServerTests`(실제 소켓·실제 HTTP 왕복)에 있다.
**전체 409 통과 / 0 실패**(1단계 전 381 → +28).

- [x] 포트 1개만 열고 — 기존 프롬프트가 `port` 없이 그대로 동작 &nbsp;`Status_WithSingleExposedPort_NeedsNoPortArgument`
- [x] 포트 2개 열고 `port` 생략 — **오류 + 열린 포트 목록** 반환, 아무 데도 전송되지 않음 &nbsp;`Send_WithTwoPortsAndNoPortArgument_IsRefusedWithPortList`
- [x] 포트 2개 열고 각각 지정 — 서로 안 섞임 &nbsp;`Call_WithNamedPort_HitsThatBridge`
- [x] 포트를 닫았다 열기 — 레지스트리 등록/해제가 새는 곳 없음 &nbsp;`ReopeningPort_DoesNotLeakRegistrations`, `Unregister_WithStaleBridge_DoesNothing`
- [x] 인스턴스 2개 — 두 번째는 `PortInUse`, 먼저 뜬 쪽은 멀쩡 &nbsp;`SecondBind_OnSamePort_ReportsPortInUse`
- [x] 9종 전부 스키마에 `port` 노출 &nbsp;`ToolsList_ExposesNineToolsWithPortArgument`
- [x] 외부 Origin 거절 / 루프백 Origin 허용 &nbsp;`ForeignOrigin_IsRejected`, `LoopbackOrigin_IsAllowed`
- [x] **실제 앱 + 실제 보드 2대(COM4·COM9)** — MCP 를 켜니 `127.0.0.1:5300` 이 열렸고, 9종 전부 `port` 노출,
      생략 시 두 포트명과 함께 거절, 포트를 지정하면 각각 자기 보드로(수신 670B vs 0B 로 구분), 외부 Origin 403.
      켜기 전에는 소켓이 열리지 않는 것도 확인 (2026-08-28)
- [ ] 마지막 포트의 MCP 를 끄면 5300 이 닫히는지
- [ ] `UartTerminal.exe` 종료 후 도구 호출 — 접속 실패가 **명확한 메시지**로
- [ ] COM 번호를 바꿔 꽂기 — 등록은 그대로, 앱에서 다시 열기만 하면 동작
- [ ] 다른 PC에서 같은 `.mcp.json` 으로 동작

마지막 두 줄이 이 작업의 목적이고, 2단계에서 확인한다.

---

## 부록. 왜 지금 구조였나

[MCP.md §1](MCP.md) 에 적힌 대로다 — **COM 포트는 한 프로세스만 열 수 있어서** MCP 서버가 WPF 프로세스 안에 있어야 했고, Claude Code는 stdio로만 서버를 띄우므로 릴레이가 필요했다.

전자는 지금도 유효하다. 후자만 HTTP로 없앨 수 있다. 즉 이 전환은 **"서버는 앱 안에" 라는 원래 판단을 유지한 채, 바깥으로 나가는 통로만 바꾸는 것**이다.
