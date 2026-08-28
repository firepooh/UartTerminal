using System.IO;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UartTerminal.Mcp;

/// <summary>HTTP 호스트의 상태. 상태 표시줄이 이 값으로 문구와 색을 고른다.</summary>
public enum McpHttpState
{
    Stopped,
    Running,
    /// <summary>포트를 다른 프로세스가 쥐고 있다 — 보통 UartTerminal 두 번째 인스턴스.</summary>
    PortInUse,
    Failed,
}

/// <summary>
/// 앱당 하나인 MCP HTTP(Streamable) 엔드포인트. 127.0.0.1 에만 바인딩한다.
///
/// <para>파이프판과 달리 서버가 <b>포트별</b>이 아니라 <b>앱별</b>이다. 어느 UART 포트로 갈지는
/// 툴의 <c>port</c> 인자와 <see cref="BridgeRegistry"/> 가 정한다(docs/mcp-http-transport.md §3).
/// 덕분에 등록이 URL 하나로 고정된다 — 체크아웃 경로도 COM 번호도 명령에 들어가지 않는다.</para>
///
/// <para>1단계에서는 Named Pipe 와 <b>병행</b>한다. 두 경로가 같은 브리지를 보므로 기존 등록이 깨지지 않는다.</para>
/// </summary>
public sealed class McpHttpServer
{
    /// <summary>MCP 엔드포인트 경로. 루트에 매핑하면 <c>/mcp</c> 가 404 가 되고 그 반대도 마찬가지라 하나를 고른다.</summary>
    private const string EndpointPath = "/mcp";

    private readonly BridgeRegistry _registry;
    private readonly object _gate = new();

    private WebApplication? _app;

    public McpHttpServer(BridgeRegistry registry, int port)
    {
        _registry = registry;
        Port = port;
    }

    public int Port { get; }

    public McpHttpState State { get; private set; } = McpHttpState.Stopped;

    /// <summary>실행 중일 때의 등록용 URL(예: <c>http://127.0.0.1:5300/mcp</c>). 그 외에는 null.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>기동 실패 사유(사람이 읽는 원문). 진단 로그와 툴팁에 쓴다.</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>등록 명령. 경로도 COM 번호도 들어가지 않아 PC 가 바뀌어도 같은 문자열이다.</summary>
    public string RegistrationCommand =>
        $"claude mcp add --transport http uart http://127.0.0.1:{Port}{EndpointPath}";

    /// <summary>
    /// 호스트의 비동기 수명주기를 <b>반드시 스레드풀에서</b> 돌린다.
    ///
    /// <para>이 메서드들은 UI 스레드(메뉴 클릭 → <c>McpSetEnabled</c>)에서 불린다. 거기서
    /// <c>StartAsync()/StopAsync()</c> 를 그냥 <c>.GetAwaiter().GetResult()</c> 로 기다리면
    /// WPF 의 <c>DispatcherSynchronizationContext</c> 가 잡혀 있어 내부 await 의 이어지는 실행이
    /// <b>UI 스레드로 돌아오려 하는데 그 스레드는 이미 막혀 있어</b> 교착한다.
    /// 실제로 앱이 통째로 멈췄다 — 소켓은 닫혔는데 그 다음 줄의 진단 로그조차 찍히지 않았다.
    /// 취소 토큰도 소용없다: 취소 콜백 역시 같은 막힌 스레드를 기다린다.</para>
    ///
    /// <para><c>Task.Run</c> 안에서는 <c>SynchronizationContext.Current</c> 가 null 이라
    /// 이어지는 실행이 스레드풀로 가고, 바깥의 대기는 <b>그냥 블록</b>이 된다(교착 아님).
    /// 이 감싸기를 "불필요한 Task.Run" 으로 보고 걷어내면 그 자리에서 앱이 다시 멈춘다.</para>
    /// </summary>
    private static T RunOffUiThread<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void RunOffUiThread(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>
    /// 호스트를 띄운다. 리스닝이 실제로 시작될 때까지 기다린다 —
    /// 포트 충돌 같은 실패를 그 자리에서 알려야 사용자가 원인을 안다.
    /// (StartAsync 대신 RunAsync 를 파이어앤포겟하면 바인드 예외가 Task 안에 갇히고,
    ///  그 다음 app.Services 를 건드리는 순간 원인과 무관한 ObjectDisposedException 이 튄다.)
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_app is not null) return;

            long t0 = Environment.TickCount64;
            WebApplication? app = null;
            try
            {
                app = Build();
                var starting = app;                       // 람다가 캡처할 non-null 사본
                RunOffUiThread(() => starting.StartAsync());

                string? bound = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
                if (bound is null) throw new InvalidOperationException("Kestrel bound no address.");

                _app = app;
                Endpoint = bound + EndpointPath;
                FailureDetail = null;
                State = McpHttpState.Running;
                DiagLog.Info($"MCP HTTP 시작: {Endpoint} ({Environment.TickCount64 - t0}ms)");
            }
            catch (Exception ex)
            {
                // 포트 충돌은 흔한 정상 실패다(두 번째 인스턴스). 앱은 그대로 살고 파이프로 계속 동작한다.
                bool inUse = ex is IOException { InnerException: AddressInUseException }
                             || ex.InnerException is AddressInUseException;
                State = inUse ? McpHttpState.PortInUse : McpHttpState.Failed;
                Endpoint = null;
                FailureDetail = ex.Message;
                DiagLog.Exception(inUse ? "McpHttpServer.Start(포트 사용 중)" : "McpHttpServer.Start", ex);
                SafeDispose(app);
            }
        }
    }

    /// <summary>
    /// 정지 유예. Kestrel 은 진행 중인 요청이 끝나기를 기다리는데, MCP Streamable HTTP 는
    /// SSE 스트림을 열어 둔 채로 산다 — 넉넉히 잡으면 <b>메뉴를 끄는 순간 UI 가 그만큼 멈춘다</b>.
    /// 연결이 없을 때 실측 정지 시간이 30ms 안쪽이므로, 1초면 정상 경로에는 충분하고
    /// 붙잡히는 경우에도 눈에 거슬리지 않는다. 이후는 Kestrel 이 연결을 끊는다.
    /// </summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 호스트를 내린다. UI 스레드에서 불리므로 <see cref="StopGrace"/> 이상 붙잡히지 않는다.
    ///
    /// <para>정지가 끝날 때까지 <see cref="_gate"/> 를 쥐고 있는다 — 놓아 버리면 곧바로 이어지는
    /// <see cref="Start"/> 가 아직 포트를 물고 있는 옛 호스트와 겹쳐 <c>AddressInUse</c> 로 실패한다
    /// (끄자마자 다시 켜는 흔한 조작이다).</para>
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            var app = _app;
            _app = null;
            State = McpHttpState.Stopped;
            Endpoint = null;
            FailureDetail = null;
            if (app is null) return;

            try
            {
                using var cts = new CancellationTokenSource(StopGrace);
                RunOffUiThread(() => app.StopAsync(cts.Token));
                DiagLog.Info("MCP HTTP 정지");
            }
            catch (Exception ex) { DiagLog.Exception("McpHttpServer.Stop", ex); }
            SafeDispose(app);
        }
    }

    private WebApplication Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // 실행 파일 옆으로 고정한다. 안 그러면 바로가기·파일 연결로 띄웠을 때
            // 현재 작업 디렉터리(C:\Windows 등)가 콘텐트 루트가 된다.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // GUI 앱이라 콘솔이 없다 — 호스트 로그는 버리고 우리 진단 로그만 쓴다.
        builder.Logging.ClearProviders();

        // 반드시 루프백. 0.0.0.0 에 열면 같은 네트워크의 누구나 UART 로 임의 명령을 쏠 수 있다.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port));

        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton<IBridgeResolver>(_registry);
        builder.Services.AddSingleton<UartMcpTools>();
        builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation
            {
                Name = "uart-terminal",
                Title = "UartTerminal",
                Version = "1.0.0",
            };
            o.ServerInstructions = UartMcpTools.HttpInstructions;
        })
        .WithHttpTransport()
        .WithTools<UartMcpTools>();

        var app = builder.Build();
        app.Use(RejectForeignOrigin);
        app.MapMcp(EndpointPath);
        return app;
    }

    /// <summary>
    /// 루프백에 열어도 <b>브라우저</b>는 닿는다 — 사용자가 방문한 웹페이지가
    /// <c>fetch('http://127.0.0.1:5300/mcp')</c> 로 UART 를 조작하려 들 수 있다(DNS rebinding).
    /// MCP 규약이 권고하는 Origin 검사를 여기서 한다: MCP 클라이언트는 Origin 을 붙이지 않고,
    /// 브라우저는 반드시 붙이므로 <b>로컬이 아닌 Origin 이 있으면 거절</b>하면 된다.
    /// </summary>
    private static async Task RejectForeignOrigin(HttpContext ctx, RequestDelegate next)
    {
        string origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsLoopbackOrigin(origin))
        {
            DiagLog.Warn($"MCP HTTP 거절 — 외부 Origin: {origin}");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(ctx).ConfigureAwait(false);
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
        (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static void SafeDispose(WebApplication? app)
    {
        if (app is null) return;
        try { ((IDisposable)app).Dispose(); } catch (Exception ex) { DiagLog.Exception("McpHttpServer.Dispose", ex); }
    }
}
