using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UartTerminal.Mcp;

/// <summary>
/// 포트별 Named Pipe(<c>uartterm-mcp-COM4</c>) 위에서 in-process MCP 서버를 운영한다(README §6 Q4).
/// COM 포트는 한 프로세스만 열 수 있으므로 MCP 서버는 반드시 이 프로세스 안에 있어야 하고,
/// stdio 를 요구하는 <c>claude mcp</c> 등록과 연결하기 위해 초소형 릴레이 exe(<c>UartTerminal.McpRelay</c>)가
/// stdio ↔ 이 Named Pipe 를 잇는다. 동적 포트/토큰/방화벽/Kestrel 문제가 모두 사라진다.
/// 파이프 ACL 은 현재 사용자 전용.
/// </summary>
public sealed class McpPipeServer
{
    private const int MaxInstances = 4;

    private readonly UartBridge _bridge;
    private readonly UartMcpTools _tools;
    private readonly string _pipeName;
    private readonly string _portName;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly SemaphoreSlim _slots = new(MaxInstances, MaxInstances);

    public McpPipeServer(UartBridge bridge, string portName)
    {
        _bridge = bridge;
        _portName = portName;
        _tools = new UartMcpTools(bridge);
        _pipeName = PipeNameFor(portName);
    }

    public bool IsRunning { get { lock (_gate) return _cts is not null; } }

    /// <summary>포트명 → Named Pipe 이름(예: COM4 → uartterm-mcp-COM4).</summary>
    public static string PipeNameFor(string portName) =>
        $"uartterm-mcp-{portName.Trim().ToUpperInvariant()}";

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _slots.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex)
            {
                _slots.Release();
                DiagLog.Exception("McpPipeServer.CreatePipe", ex);
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SafeDispose(pipe);
                _slots.Release();
                break;
            }
            catch (Exception ex)
            {
                SafeDispose(pipe);
                _slots.Release();
                DiagLog.Exception("McpPipeServer.WaitForConnection", ex);
                continue;
            }

            // 연결을 백그라운드로 처리하고 즉시 다음 인스턴스를 수락 대기
            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(pipe, ct).ConfigureAwait(false); }
                finally
                {
                    SafeDispose(pipe);
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var options = BuildOptions();
            await using var transport = new StreamServerTransport(pipe, pipe, $"uart-{_portName}");
            await using var server = McpServer.Create(transport, options);
            DiagLog.Info($"MCP 클라이언트 연결됨: {_pipeName}");
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 정지/해제 */ }
        catch (Exception ex)
        {
            DiagLog.Exception("McpPipeServer.HandleConnection", ex);
        }
        finally
        {
            DiagLog.Info($"MCP 클라이언트 연결 종료: {_pipeName}");
        }
    }

    private McpServerOptions BuildOptions()
    {
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var mi in typeof(UartMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (mi.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
            toolCollection.Add(McpServerTool.Create(mi, _tools));
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = $"uart-terminal-{_portName}",
                Title = $"UartTerminal {_portName}",
                Version = "1.0.0",
            },
            ServerInstructions =
                "이 서버는 사용자가 열어 둔 UART 포트(" + _portName + ")를 사용자와 공유한다. " +
                "송신(uart_send)은 화면에 [AI→] 로 표시되고 사용자 입력과 원자적으로 섞인다. " +
                "수신은 단조 증가 커서로 읽는다: uart_read/uart_expect 가 돌려준 cursor 를 다음 호출에 넘겨 이어서 읽고, " +
                "dropped_bytes 가 0 이 아니면 버퍼 용량 초과로 유실이 있었다는 뜻이다. " +
                "명령 응답을 기다릴 때는 uart_send 후 uart_expect 를 쓰면 폴링 왕복을 줄일 수 있다.",
            ToolCollection = toolCollection,
        };
    }

    private NamedPipeServerStream CreatePipe()
    {
        // 현재 사용자만 접근 가능한 ACL
        var security = new PipeSecurity();
        var self = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("현재 사용자 SID를 확인할 수 없습니다.");
        security.AddAccessRule(new PipeAccessRule(
            self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 1 << 16,
            outBufferSize: 1 << 16,
            pipeSecurity: security);
    }

    private static void SafeDispose(NamedPipeServerStream pipe)
    {
        try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
        try { pipe.Dispose(); } catch { }
    }
}
