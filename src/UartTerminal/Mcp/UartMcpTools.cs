using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UartTerminal.Mcp;

/// <summary>
/// AI(Claude Code)가 사용자와 같은 UART 포트를 공유해 읽고 쓰는 MCP 툴 8종(README 기능 3 / Phase B).
/// 실제 동작은 스레드 안전 <see cref="UartBridge"/>에 위임한다. 각 툴 메서드는 MCP 서버 스레드에서 호출된다.
/// 반환 객체는 JSON(snake_case)으로 직렬화되어 AI 에게 전달된다.
/// </summary>
[McpServerToolType]
public sealed class UartMcpTools
{
    private readonly UartBridge _bridge;

    public UartMcpTools(UartBridge bridge) => _bridge = bridge;

    [McpServerTool(Name = "uart_status", ReadOnly = true, OpenWorld = false)]
    [Description("현재 UART 연결 상태와 수신 버퍼 커서 정보를 반환한다. " +
                 "end_cursor 를 기억해 uart_read/uart_expect 의 cursor 로 넘기면 이어서 읽을 수 있다.")]
    public StatusResult Status() => _bridge.Status();

    [McpServerTool(Name = "uart_send", Destructive = true, OpenWorld = false)]
    [Description("UART 로 텍스트를 전송한다(사용자 입력과 동일한 단일 TX 큐로 원자적 전송). " +
                 "전송 내용은 터미널 화면에 [AI→] 메타 라인으로 표시된다. " +
                 "append_newline=true 면 끝에 개행(CR)을 붙여 esp_console 명령처럼 실행되게 한다. " +
                 "MCP 가 읽기 전용이거나 포트가 연결되지 않았으면 error 를 반환한다.")]
    public SendResult Send(
        [Description("전송할 텍스트. 내부 개행은 CR 로 정규화된다.")] string text,
        [Description("끝에 개행(CR)을 붙일지 여부. 명령 실행 시 true. 기본 true.")] bool append_newline = true)
        => _bridge.Send(text, append_newline);

    [McpServerTool(Name = "uart_read", ReadOnly = true, OpenWorld = false)]
    [Description("수신 버퍼를 커서 기준으로 읽는다. cursor 를 생략하면 보관 중인 가장 오래된 위치부터 읽고, " +
                 "반환된 cursor 를 다음 호출에 넘기면 새 데이터만 이어서 받는다. " +
                 "링버퍼 용량을 넘겨 유실된 바이트는 dropped_bytes 로 명시된다. " +
                 "strip_ansi(기본 true)면 ANSI 이스케이프/제어 문자를 제거한 평문을 반환한다. " +
                 "more=true 면 아직 읽지 않은 데이터가 더 있다.")]
    public ReadResult Read(
        [Description("읽기 시작 커서(절대 바이트 오프셋). 생략 시 보관된 가장 오래된 위치.")] long? cursor = null,
        [Description("한 번에 읽을 최대 바이트. 기본 8192.")] int max_bytes = 8192,
        [Description("ANSI 이스케이프/제어 문자 제거 여부. 기본 true.")] bool strip_ansi = true)
        => _bridge.Read(cursor, max_bytes, strip_ansi);

    [McpServerTool(Name = "uart_expect", ReadOnly = true, OpenWorld = false)]
    [Description("정규식 패턴이 수신 스트림에 나타날 때까지 기다린다(폴링 왕복 최소화). " +
                 "cursor 를 생략하면 '지금 이후' 도착하는 데이터를 기다린다(예: 명령을 uart_send 한 뒤 응답 대기). " +
                 "matched=true 면 match/groups 에 결과가, timed_out=true 면 timeout 까지 못 찾은 것이다. " +
                 "data 에는 그동안 관측한 텍스트가 담긴다.")]
    public Task<ExpectResult> Expect(
        [Description("찾을 패턴. regex=true(기본)면 .NET 정규식, false 면 리터럴 문자열.")] string pattern,
        [Description("대기 시간(ms). 기본 5000.")] int timeout_ms = 5000,
        [Description("탐색 시작 커서. 생략 시 지금 이후 도착분.")] long? cursor = null,
        [Description("ANSI 이스케이프 제거 여부. 기본 true.")] bool strip_ansi = true,
        [Description("패턴을 정규식으로 해석할지. 기본 true.")] bool regex = true,
        CancellationToken cancellationToken = default)
        => _bridge.ExpectAsync(pattern, timeout_ms, cursor, strip_ansi, regex, cancellationToken);

    [McpServerTool(Name = "uart_screen", ReadOnly = true, OpenWorld = false)]
    [Description("현재 터미널 화면(논리 라인 버퍼)의 최근 내용을 사람이 보는 형태로 스냅샷한다. " +
                 "CR 덮어쓰기/커서 편집이 반영된 결과라 프롬프트·표 같은 화면 상태 파악에 적합하다. " +
                 "raw 바이트 스트림이 필요하면 uart_read 를 쓴다.")]
    public ScreenResult Screen(
        [Description("반환할 최근 논리 라인 수. 기본 50, 최대 2000.")] int max_lines = 50)
        => _bridge.Screen(max_lines);

    [McpServerTool(Name = "uart_set_dtr_rts", Destructive = true, OpenWorld = false)]
    [Description("DTR/RTS 제어선을 설정한다. ESP32 보드의 리셋/부트로더 진입 시퀀스에 쓰인다(주의: 보드가 리셋될 수 있음). " +
                 "MCP 가 읽기 전용이거나 포트가 연결되지 않았으면 error 를 반환한다.")]
    public DtrRtsResult SetDtrRts(
        [Description("DTR 라인 상태.")] bool dtr,
        [Description("RTS 라인 상태.")] bool rts)
        => _bridge.SetDtrRts(dtr, rts);

    [McpServerTool(Name = "uart_close", Destructive = true, OpenWorld = false)]
    [Description("터미널이 점유한 UART 포트를 닫아 다른 프로그램(예: esptool)이 열 수 있게 양보한다. " +
                 "COM 포트는 한 프로세스만 열 수 있으므로, ESP32 펌웨어 플래싱처럼 포트 독점이 필요한 " +
                 "외부 작업 전에 반드시 호출해야 한다. 반환 후에는 포트가 해제돼 esptool 등을 바로 실행할 수 있다. " +
                 "닫혀 있는 동안 자동 재연결(USB 재접속 감시)은 일시 중지되며, 작업이 끝나면 uart_open 으로 다시 연다. " +
                 "state 는 closed(방금 닫음) 또는 already_closed(이미 닫혀 있었음). " +
                 "MCP 가 읽기 전용이거나 비활성이면 error 를 반환한다.")]
    public Task<PortActionResult> Close() => _bridge.ClosePortAsync();

    [McpServerTool(Name = "uart_open", Destructive = true, OpenWorld = false)]
    [Description("uart_close 로 양보했거나 끊긴 포트를 같은 포트명·같은 설정으로 다시 연다. " +
                 "외부 작업(플래싱 등)이 끝난 뒤 호출한다. " +
                 "state 는 open(방금 열림) 또는 already_open(이미 열려 있었음). " +
                 "포트가 아직 다른 프로그램(esptool 등)에 점유돼 있으면 ok=false, state=in_use, error=in_use 를 " +
                 "반환하니 잠시(수백 ms) 후 재시도한다. " +
                 "MCP 가 읽기 전용이거나 비활성이면 error 를 반환한다.")]
    public Task<PortActionResult> Open() => _bridge.OpenPortAsync();
}
