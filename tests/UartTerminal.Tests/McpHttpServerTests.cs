using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using UartTerminal.Core.Terminal;
using UartTerminal.Mcp;
using Xunit;

namespace UartTerminal.Tests;

/// <summary>
/// 실제로 소켓을 열고 HTTP 로 왕복한다. 단위 테스트로 규칙만 확인하면
/// "규칙은 맞는데 엔드포인트가 안 뜬다" 를 못 잡는다 — 1단계에서 확인해야 하는 것이 바로 그것이다.
///
/// <para>포트는 0 으로 연다(OS 가 빈 포트를 고른다). 고정 포트로 열면 개발 PC 에서 앱이 떠 있을 때
/// 테스트가 포트 충돌로 깨진다.</para>
/// </summary>
public class McpHttpServerTests : IDisposable
{
    private readonly BridgeRegistry _registry = new();
    private readonly McpHttpServer _server;
    private readonly HttpClient _http = new();

    public McpHttpServerTests()
    {
        _server = new McpHttpServer(_registry, 0);
    }

    public void Dispose()
    {
        _server.Stop();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    private static UartBridge NewBridge() => new(new TerminalEngine(new UTF8Encoding(false)));

    /// <summary>노출이 없으면 소켓도 열지 않는 것이 앱의 정책이라, 테스트는 직접 띄운다.</summary>
    private string StartAndGetEndpoint()
    {
        _server.Start();
        Assert.Equal(McpHttpState.Running, _server.State);
        Assert.NotNull(_server.Endpoint);
        return _server.Endpoint!;
    }

    /// <summary>
    /// MCP Streamable HTTP 한 번 왕복. 응답은 JSON 이 아니라 SSE 프레임(<c>event:</c>/<c>data:</c>)으로 온다.
    /// </summary>
    private async Task<JsonElement> RpcAsync(string endpoint, string body, string? sessionId = null,
                                             string? origin = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (sessionId is not null) req.Headers.Add("Mcp-Session-Id", sessionId);
        if (origin is not null) req.Headers.Add("Origin", origin);

        using var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        string text = await res.Content.ReadAsStringAsync();
        string? data = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        Assert.True(data is not null, $"SSE data 프레임이 없다: {text}");

        var doc = JsonDocument.Parse(data!["data:".Length..].Trim());
        if (res.Headers.TryGetValues("Mcp-Session-Id", out var ids)) LastSessionId = ids.First();
        return doc.RootElement.Clone();
    }

    private string? LastSessionId;

    private async Task<string> HandshakeAsync(string endpoint)
    {
        var init = await RpcAsync(endpoint,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        Assert.Equal("uart-terminal", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.NotNull(LastSessionId);
        return LastSessionId!;
    }

    // ── 기동 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_BindsLoopbackOnly()
    {
        string endpoint = StartAndGetEndpoint();

        Assert.StartsWith("http://127.0.0.1:", endpoint);
        Assert.EndsWith("/mcp", endpoint);
    }

    /// <summary>등록 명령에 PC 마다 달라지는 값이 없어야 한다 — 이 전환의 목적이다.</summary>
    [Fact]
    public void RegistrationCommand_HasNoMachineSpecificValues()
    {
        var fixedPort = new McpHttpServer(_registry, 5300);

        Assert.Equal("claude mcp add --transport http uart http://127.0.0.1:5300/mcp",
                     fixedPort.RegistrationCommand);
    }

    /// <summary>
    /// <b>회귀:</b> 메뉴에서 MCP 를 끄면 앱이 통째로 멈췄다(2026-08-28, 실사용에서 발견).
    ///
    /// <para>원인은 UI 스레드에서의 sync-over-async 였다. <c>StopAsync()</c> 를 그 자리에서
    /// <c>.GetAwaiter().GetResult()</c> 로 기다리면, WPF 의 <c>DispatcherSynchronizationContext</c> 때문에
    /// 내부 await 의 이어지는 실행이 <b>이미 막혀 있는 UI 스레드</b>로 돌아오려 해 교착한다.
    /// 증상이 고약했다 — 소켓은 닫혔는데 바로 다음 줄의 진단 로그조차 찍히지 않아
    /// "코드가 실행되지 않은 것처럼" 보였다.</para>
    ///
    /// <para>그래서 진짜 Dispatcher 스레드에서 돌린다. 단위 테스트 스레드(SynchronizationContext 없음)에서는
    /// 이 버그가 <b>재현되지 않는다</b> — 그게 실사용에서야 드러난 이유다.</para>
    ///
    /// <para>공용 <see cref="_server"/> 가 아니라 <b>이 테스트만의 서버</b>를 쓴다. 교착이 되살아나면
    /// 디스패처 스레드가 서버의 잠금을 쥔 채 굳는데, 공용 서버였다면 <see cref="Dispose"/> 가 그 잠금을 기다리다
    /// <b>테스트 실행 전체가 멎는다</b>(고치기 전 코드로 실제로 겪었다). 실패는 실패로 끝나야 한다.</para>
    /// </summary>
    [Fact]
    public void StartAndStop_OnWpfDispatcherThread_DoNotDeadlock()
    {
        var solo = new McpHttpServer(new BridgeRegistry(), 0);
        var ready = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        Dispatcher? ui = null;
        Exception? failure = null;
        bool hadDispatcherContext = false;

        var thread = new Thread(() =>
        {
            ui = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        ui!.BeginInvoke(() =>
        {
            // 이 값이 false 면 재현 조건이 안 갖춰진 것이라 테스트가 의미를 잃는다.
            hadDispatcherContext = SynchronizationContext.Current is DispatcherSynchronizationContext;
            try
            {
                solo.Start();
                solo.Stop();
            }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });

        bool finished = done.Wait(TimeSpan.FromSeconds(20));
        ui.InvokeShutdown();

        Assert.True(finished, "UI 스레드에서 Start/Stop 이 끝나지 않았다 — 교착(앱이 멈춘다)");
        Assert.Null(failure);
        Assert.True(hadDispatcherContext, "DispatcherSynchronizationContext 가 안 깔려 재현 조건이 아니다");
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        StartAndGetEndpoint();
        _server.Stop();
        _server.Stop();

        Assert.Equal(McpHttpState.Stopped, _server.State);
        Assert.Null(_server.Endpoint);
    }

    /// <summary>두 번째 인스턴스는 HTTP 를 포기하고 앱은 그대로 산다(§5 결정).</summary>
    [Fact]
    public void SecondBind_OnSamePort_ReportsPortInUse()
    {
        string endpoint = StartAndGetEndpoint();
        int taken = new Uri(endpoint).Port;

        var second = new McpHttpServer(new BridgeRegistry(), taken);
        try
        {
            second.Start();
            Assert.Equal(McpHttpState.PortInUse, second.State);
            Assert.Null(second.Endpoint);
            Assert.False(string.IsNullOrEmpty(second.FailureDetail));
        }
        finally { second.Stop(); }

        Assert.Equal(McpHttpState.Running, _server.State);   // 먼저 뜬 쪽은 멀쩡하다
    }

    // ── 프로토콜 왕복 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolsList_ExposesNineToolsWithPortArgument()
    {
        string endpoint = StartAndGetEndpoint();
        string sid = await HandshakeAsync(endpoint);

        var res = await RpcAsync(endpoint, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", sid);
        var tools = res.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

        Assert.Equal(9, tools.Count);
        foreach (var t in tools)
        {
            string name = t.GetProperty("name").GetString()!;
            Assert.StartsWith("uart_", name);
            Assert.True(t.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("port", out _),
                        $"{name} 스키마에 port 가 없다");
        }
    }

    [Fact]
    public async Task Status_WithSingleExposedPort_NeedsNoPortArgument()
    {
        _registry.Register("COM4", NewBridge());
        string endpoint = StartAndGetEndpoint();
        string sid = await HandshakeAsync(endpoint);

        var res = await RpcAsync(endpoint,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"uart_status","arguments":{}}}""", sid);

        var result = res.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var err) && err.GetBoolean());
    }

    /// <summary>
    /// 이 전환에서 가장 위험한 지점(§3.3). 포트가 둘일 때 port 를 생략하면
    /// <b>아무 데도 보내지 않고</b> 목록과 함께 거절해야 한다.
    /// </summary>
    [Fact]
    public async Task Send_WithTwoPortsAndNoPortArgument_IsRefusedWithPortList()
    {
        _registry.Register("COM4", NewBridge());
        _registry.Register("COM7", NewBridge());
        string endpoint = StartAndGetEndpoint();
        string sid = await HandshakeAsync(endpoint);

        var res = await RpcAsync(endpoint,
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"uart_send","arguments":{"text":"reboot"}}}""", sid);

        var result = res.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        string message = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("COM4", message);
        Assert.Contains("COM7", message);   // 어디로 보낼지 고르라고 목록을 준다
    }

    [Fact]
    public async Task Call_WithUnknownPort_IsRefused()
    {
        _registry.Register("COM4", NewBridge());
        string endpoint = StartAndGetEndpoint();
        string sid = await HandshakeAsync(endpoint);

        var res = await RpcAsync(endpoint,
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"uart_status","arguments":{"port":"COM9"}}}""", sid);

        Assert.True(res.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    /// <summary>포트를 지정하면 서로 섞이지 않는다.</summary>
    [Fact]
    public async Task Call_WithNamedPort_HitsThatBridge()
    {
        var com4 = NewBridge();
        var com7 = NewBridge();
        com4.ReadOnly = true;               // 두 브리지를 응답으로 구분할 표식
        _registry.Register("COM4", com4);
        _registry.Register("COM7", com7);

        string endpoint = StartAndGetEndpoint();
        string sid = await HandshakeAsync(endpoint);

        Assert.True(await ReadOnlyFlagAsync(endpoint, sid, "COM4", 6));
        Assert.False(await ReadOnlyFlagAsync(endpoint, sid, "COM7", 7));
    }

    private async Task<bool> ReadOnlyFlagAsync(string endpoint, string sid, string port, int id)
    {
        // 원시 문자열 보간은 닫는 중괄호가 셋이라 $$ 로는 애매해진다 — 이 한 줄만 이어 붙인다.
        string body = "{\"jsonrpc\":\"2.0\",\"id\":" + id +
                      ",\"method\":\"tools/call\",\"params\":{\"name\":\"uart_status\"," +
                      "\"arguments\":{\"port\":\"" + port + "\"}}}";
        var res = await RpcAsync(endpoint, body, sid);

        string json = res.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(json).RootElement.GetProperty("read_only").GetBoolean();
    }

    // ── 보안 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 루프백에 열어도 브라우저는 닿는다. 사용자가 방문한 웹페이지가 UART 를 조작하지 못하도록
    /// MCP 규약이 권고하는 Origin 검사를 한다.
    /// </summary>
    [Fact]
    public async Task ForeignOrigin_IsRejected()
    {
        string endpoint = StartAndGetEndpoint();

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
                                        Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Headers.Add("Origin", "https://evil.example.com");

        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task LoopbackOrigin_IsAllowed()
    {
        string endpoint = StartAndGetEndpoint();

        var res = await RpcAsync(endpoint,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
            origin: "http://localhost:5173");

        Assert.True(res.TryGetProperty("result", out _));
    }
}
