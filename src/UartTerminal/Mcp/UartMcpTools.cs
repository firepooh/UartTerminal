using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace UartTerminal.Mcp;

/// <summary>
/// AI(Claude Code)가 사용자와 같은 UART 포트를 공유해 읽고 쓰는 MCP 툴 9종(README 기능 3 / Phase B).
/// 실제 동작은 스레드 안전 <see cref="UartBridge"/>에 위임한다. 각 툴 메서드는 MCP 서버 스레드에서 호출된다.
/// 반환 객체는 JSON(snake_case)으로 직렬화되어 AI 에게 전달된다.
///
/// <para>어느 브리지로 갈지는 <see cref="IBridgeResolver"/> 가 정한다. Named Pipe 는 파이프가 이미
/// 포트 하나에 묶여 있어 고를 것이 없고, HTTP 는 엔드포인트 하나가 열린 포트 전부를 보므로
/// 마지막 선택 인자 <c>port</c> 로 지정한다(docs/mcp-http-transport.md §3.2).
/// 기본값이 <c>null</c> 이라 포트를 하나만 연 흔한 경우에는 기존 프롬프트가 그대로 동작한다.</para>
/// </summary>
[McpServerToolType]
public sealed class UartMcpTools
{
    private readonly IBridgeResolver _resolver;

    public UartMcpTools(IBridgeResolver resolver) => _resolver = resolver;

    /// <summary>단일 브리지용 편의 생성자(Named Pipe 경로).</summary>
    public UartMcpTools(UartBridge bridge, string portName)
        : this(new SingleBridgeResolver(bridge, portName)) { }

    /// <summary>
    /// <c>port</c> 인자를 브리지로 바꾼다. 못 고르면 <b>아무 데도 보내지 않고</b> 예외를 던진다 —
    /// SDK 가 이를 <c>isError</c> 응답으로 바꿔 AI 에게 전달하므로, AI 는 목록을 보고 다시 부르면 된다.
    /// 추측해서 하나를 고르지 않는 이유는 docs/mcp-http-transport.md §3.3 에 적어 뒀다.
    /// </summary>
    private UartBridge Resolve(string? port)
    {
        if (_resolver.TryResolve(port, out var bridge, out var reason) && bridge is not null) return bridge;

        string list = string.Join(", ", _resolver.ExposedPorts);
        throw reason switch
        {
            BridgeResolution.NoneExposed => new McpException(
                "MCP 에 노출된 포트가 없습니다. UartTerminal 에서 포트를 열고 [MCP] 메뉴의 " +
                "'MCP 서버 활성화'를 켠 뒤 다시 시도하세요."),

            BridgeResolution.Ambiguous => new McpException(
                $"열린 포트가 여러 개입니다({list}). 어느 보드인지 알 수 없어 아무것도 보내지 않았습니다 — " +
                "port 인자로 하나를 지정해 다시 호출하세요. uart_status 로 각 포트의 상태를 먼저 확인할 수 있습니다."),

            _ => new McpException(
                $"포트 '{port}' 는 MCP 에 노출돼 있지 않습니다. 현재 노출된 포트: " +
                (list.Length == 0 ? "(없음)" : list)),
        };
    }

    /// <summary>
    /// HTTP 엔드포인트용 서버 안내문. 파이프판(<see cref="McpPipeServer"/>)과 갈리는 곳은 하나 —
    /// <b>엔드포인트가 앱당 하나라 포트를 지정해야 한다</b>는 점이다.
    /// </summary>
    public static string HttpInstructions =>
        "이 서버는 사용자가 UartTerminal 에서 열어 둔 UART 포트를 사용자와 공유한다. " +
        "엔드포인트는 앱당 하나이고 포트가 여러 개 열려 있을 수 있으므로, 모든 툴의 마지막 인자 port 로 대상을 지정한다. " +
        "port 를 생략하면 열린 포트가 하나일 때만 그 포트로 가고, 둘 이상이면 오류와 함께 목록을 돌려준다(임의로 고르지 않는다). " +
        "먼저 uart_status 를 port 없이 불러 무엇이 열려 있는지 확인하고, 이후 호출에 port 를 실어 보내는 흐름을 권한다. " +
        "송신(uart_send)은 화면에 [AI→] 로 표시되고 사용자 입력과 원자적으로 섞인다. " +
        "수신은 단조 증가 커서로 읽는다: uart_read/uart_expect 가 돌려준 cursor 를 다음 호출에 넘겨 이어서 읽고, " +
        "dropped_bytes 가 0 이 아니면 버퍼 용량 초과로 유실이 있었다는 뜻이다. " +
        "명령 응답을 기다릴 때는 uart_send 후 uart_expect 를 쓰면 폴링 왕복을 줄일 수 있다. " +
        "펌웨어 플래싱(esptool 등 포트를 독점하는 외부 도구)이 필요하면: uart_close 로 포트를 양보 → " +
        "셸에서 esptool 실행 → uart_open 으로 재연결한다. " +
        "uart_open 이 in_use 를 반환하면 외부 도구가 아직 포트를 쥐고 있는 것이니 잠시 후 재시도한다.";

    private const string PortArg =
        "대상 UART 포트(예: \"COM4\"). 생략하면 열린 포트가 하나일 때 그 포트로 가고, " +
        "둘 이상이면 오류와 함께 열린 포트 목록을 돌려준다.";

    [McpServerTool(Name = "uart_status", ReadOnly = true, OpenWorld = false)]
    [Description("현재 UART 연결 상태와 수신 버퍼 커서 정보를 반환한다. " +
                 "end_cursor 를 기억해 uart_read/uart_expect 의 cursor 로 넘기면 이어서 읽을 수 있다. " +
                 "port 를 생략했는데 열린 포트가 여러 개면 오류로 목록을 돌려주므로, 무엇이 열려 있는지 파악하는 데도 쓴다.")]
    public StatusResult Status(
        [Description(PortArg)] string? port = null)
        => Resolve(port).Status();

    [McpServerTool(Name = "uart_send", Destructive = true, OpenWorld = false)]
    [Description("UART 로 텍스트를 전송한다(사용자 입력과 동일한 단일 TX 큐로 원자적 전송). " +
                 "전송 내용은 터미널 화면에 [AI→] 메타 라인으로 표시된다. " +
                 "append_newline=true 면 끝에 개행(CR)을 붙여 esp_console 명령처럼 실행되게 한다. " +
                 "MCP 가 읽기 전용이거나 포트가 연결되지 않았으면 error 를 반환한다.")]
    public SendResult Send(
        [Description("전송할 텍스트. 내부 개행은 CR 로 정규화된다.")] string text,
        [Description("끝에 개행(CR)을 붙일지 여부. 명령 실행 시 true. 기본 true.")] bool append_newline = true,
        [Description(PortArg)] string? port = null)
        => Resolve(port).Send(text, append_newline);

    [McpServerTool(Name = "uart_read", ReadOnly = true, OpenWorld = false)]
    [Description("수신 버퍼를 커서 기준으로 읽는다. cursor 를 생략하면 보관 중인 가장 오래된 위치부터 읽고, " +
                 "반환된 cursor 를 다음 호출에 넘기면 새 데이터만 이어서 받는다. " +
                 "링버퍼 용량을 넘겨 유실된 바이트는 dropped_bytes 로 명시된다. " +
                 "strip_ansi(기본 true)면 ANSI 이스케이프/제어 문자를 제거한 평문을 반환한다. " +
                 "more=true 면 아직 읽지 않은 데이터가 더 있다.")]
    public ReadResult Read(
        [Description("읽기 시작 커서(절대 바이트 오프셋). 생략 시 보관된 가장 오래된 위치.")] long? cursor = null,
        [Description("한 번에 읽을 최대 바이트. 기본 8192.")] int max_bytes = 8192,
        [Description("ANSI 이스케이프/제어 문자 제거 여부. 기본 true.")] bool strip_ansi = true,
        [Description(PortArg)] string? port = null)
        => Resolve(port).Read(cursor, max_bytes, strip_ansi);

    [McpServerTool(Name = "uart_expect", ReadOnly = true, OpenWorld = false)]
    [Description("정규식 패턴이 수신 스트림에 나타날 때까지 기다린다(폴링 왕복 최소화). " +
                 "cursor 를 생략하면 '지금 이후' 도착하는 데이터를 기다린다(예: 명령을 uart_send 한 뒤 응답 대기). " +
                 "matched=true 면 match/groups 에 결과가, timed_out=true 면 timeout 까지 못 찾은 것이다. " +
                 "data 에는 그동안 관측한 텍스트가 담긴다.\n" +
                 "주의(조기 매칭): 수신은 스트림이라 도착하는 대로 평가한다. 따라서 열린 수량자로 끝나는 패턴은 " +
                 "값이 다 도착하기 전에 걸린다 — 'mode\\s+:\\s+\\w+' 는 'CDC' 의 'C' 만 온 시점에 'mode : C' 로 매칭된다. " +
                 "값이 줄 단위로 오면 match_mode=\"line\" 을 쓰거나, 패턴을 완결형으로 쓰라: " +
                 "개행을 포함('mode\\s+:\\s+\\w+\\r?\\n') 하거나 후보를 열거('(CDC|MSC)').")]
    public Task<ExpectResult> Expect(
        [Description("찾을 패턴. regex=true(기본)면 .NET 정규식, false 면 리터럴 문자열.")] string pattern,
        [Description("대기 시간(ms). 기본 5000.")] int timeout_ms = 5000,
        [Description("탐색 시작 커서. 생략 시 지금 이후 도착분.")] long? cursor = null,
        [Description("ANSI 이스케이프 제거 여부. 기본 true.")] bool strip_ansi = true,
        [Description("패턴을 정규식으로 해석할지. 기본 true.")] bool regex = true,
        [Description("\"stream\"(기본)=도착하는 대로 평가(프롬프트처럼 개행 없는 출력도 대기 가능). " +
                     "\"line\"=완성된 줄까지만 평가해 조기 매칭을 없앤다. " +
                     "단 line 은 개행이 오지 않는 프롬프트('xcp> ')에는 영원히 매칭되지 않으니 그때는 stream 을 쓸 것.")]
        string match_mode = "stream",
        [Description(PortArg)] string? port = null,
        CancellationToken cancellationToken = default)
        => Resolve(port).ExpectAsync(pattern, timeout_ms, cursor, strip_ansi, regex, cancellationToken,
                                     lineMode: string.Equals(match_mode, "line", StringComparison.OrdinalIgnoreCase));

    [McpServerTool(Name = "uart_screen", ReadOnly = true, OpenWorld = false)]
    [Description("현재 터미널 화면(논리 라인 버퍼)의 최근 내용을 사람이 보는 형태로 스냅샷한다. " +
                 "CR 덮어쓰기/커서 편집이 반영된 결과라 프롬프트·표 같은 화면 상태 파악에 적합하다. " +
                 "raw 바이트 스트림이 필요하면 uart_read 를 쓴다.")]
    public ScreenResult Screen(
        [Description("반환할 최근 논리 라인 수. 기본 50, 최대 2000.")] int max_lines = 50,
        [Description(PortArg)] string? port = null)
        => Resolve(port).Screen(max_lines);

    [McpServerTool(Name = "uart_set_dtr_rts", Destructive = true, OpenWorld = false)]
    [Description("DTR/RTS 제어선을 설정한다. ESP32 보드의 리셋/부트로더 진입 시퀀스에 쓰인다(주의: 보드가 리셋될 수 있음). " +
                 "MCP 가 읽기 전용이거나 포트가 연결되지 않았으면 error 를 반환한다.")]
    public DtrRtsResult SetDtrRts(
        [Description("DTR 라인 상태.")] bool dtr,
        [Description("RTS 라인 상태.")] bool rts,
        [Description(PortArg)] string? port = null)
        => Resolve(port).SetDtrRts(dtr, rts);

    [McpServerTool(Name = "uart_reset", Destructive = true, OpenWorld = false)]
    [Description("ESP32 보드를 DTR/RTS 자동 리셋 회로로 리셋한다(EN 펄스 100ms → 해제 50ms). " +
                 "bootloader=false(기본)면 평소처럼 펌웨어를 재부팅하므로 부팅 로그를 처음부터 관측할 수 있고, " +
                 "bootloader=true 면 IO0=LOW 상태로 리셋을 풀어 다운로드(플래싱) 모드로 진입한다. " +
                 "uart_set_dtr_rts 를 여러 번 호출하는 것과 달리 대기 시간이 보장된다. " +
                 "MCP 가 읽기 전용이거나 포트가 연결되지 않았으면 error 를 반환한다.")]
    public Task<ResetResult> Reset(
        [Description("true 면 부트로더(다운로드) 모드로 진입. 기본 false(일반 리셋).")] bool bootloader = false,
        [Description(PortArg)] string? port = null,
        CancellationToken cancellationToken = default)
        => Resolve(port).ResetAsync(bootloader, cancellationToken);

    [McpServerTool(Name = "uart_close", Destructive = true, OpenWorld = false)]
    [Description("터미널이 점유한 UART 포트를 닫아 다른 프로그램(예: esptool)이 열 수 있게 양보한다. " +
                 "COM 포트는 한 프로세스만 열 수 있으므로, ESP32 펌웨어 플래싱처럼 포트 독점이 필요한 " +
                 "외부 작업 전에 반드시 호출해야 한다. 반환 후에는 포트가 해제돼 esptool 등을 바로 실행할 수 있다. " +
                 "닫혀 있는 동안 자동 재연결(USB 재접속 감시)은 일시 중지되며, 작업이 끝나면 uart_open 으로 다시 연다. " +
                 "state 는 closed(방금 닫음) 또는 already_closed(이미 닫혀 있었음). " +
                 "MCP 가 읽기 전용이거나 비활성이면 error 를 반환한다.")]
    public Task<PortActionResult> Close(
        [Description(PortArg)] string? port = null)
        => Resolve(port).ClosePortAsync();

    [McpServerTool(Name = "uart_open", Destructive = true, OpenWorld = false)]
    [Description("uart_close 로 양보했거나 끊긴 포트를 같은 포트명·같은 설정으로 다시 연다. " +
                 "외부 작업(플래싱 등)이 끝난 뒤 호출한다. " +
                 "state 는 open(방금 열림) 또는 already_open(이미 열려 있었음). " +
                 "포트가 아직 다른 프로그램(esptool 등)에 점유돼 있으면 ok=false, state=in_use, error=in_use 를 " +
                 "반환하니 잠시(수백 ms) 후 재시도한다. " +
                 "MCP 가 읽기 전용이거나 비활성이면 error 를 반환한다.")]
    public Task<PortActionResult> Open(
        [Description(PortArg)] string? port = null)
        => Resolve(port).OpenPortAsync();
}
