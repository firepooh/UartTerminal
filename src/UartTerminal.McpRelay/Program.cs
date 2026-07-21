using System.IO.Pipes;

// ─────────────────────────────────────────────────────────────────────────────
// UartTerminal MCP stdio 릴레이 (README §6 Q4)
//
// Claude Code 는 MCP 서버를 stdio 로 실행하지만, UART 포트를 실제로 여는 MCP 서버는
// UartTerminal WPF 프로세스 안(in-process)에 있어야 한다(COM 포트는 한 프로세스만 오픈 가능).
// 이 릴레이는 stdin/stdout ↔ 인스턴스별 Named Pipe(uartterm-mcp-COM4) 사이를 잇는
// 순수 바이트 펌프다. JSON-RPC 는 그대로 통과한다.
//
// 사용: UartTerminal.McpRelay.exe COM4
//   claude mcp add uart-com4 -- "C:\path\UartTerminal.McpRelay.exe" COM4
// ─────────────────────────────────────────────────────────────────────────────

const int ConnectTimeoutMs = 10_000;

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("usage: UartTerminal.McpRelay <PORT>   (예: UartTerminal.McpRelay COM4)");
    return 2;
}

string port = args[0].Trim().ToUpperInvariant();
// McpPipeServer.PipeNameFor 와 동일 규칙(중복을 피하려 규칙을 한 줄로 고정).
string pipeName = $"uartterm-mcp-{port}";

await using var pipe = new NamedPipeClientStream(
    serverName: ".",
    pipeName: pipeName,
    direction: PipeDirection.InOut,
    options: PipeOptions.Asynchronous);

try
{
    await pipe.ConnectAsync(ConnectTimeoutMs);
}
catch (TimeoutException)
{
    Console.Error.WriteLine(
        $"UartTerminal 에 연결하지 못했습니다(pipe: {pipeName}). " +
        "UartTerminal 이 실행 중이고 해당 포트가 열려 있으며 MCP 가 활성화되어 있는지 확인하세요.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"파이프 연결 오류({pipeName}): {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

// 양방향 펌프. 한쪽이 끝나면(EOF/파이프 종료) 반대쪽도 취소하고 종료한다.
var toPipe = PumpAsync(stdin, pipe, cts);
var toStdout = PumpAsync(pipe, stdout, cts);

await Task.WhenAny(toPipe, toStdout);
cts.Cancel();
// 리다이렉트된 stdin 의 ReadAsync 는 취소 토큰을 신뢰성 있게 관측하지 못한다(Windows).
// 파이프 쪽이 먼저 닫힌 경우 stdin 펌프가 블록될 수 있으므로, 정리를 짧게만 기다리고 반환한다.
// (프로세스 종료 시 남은 블로킹 읽기는 함께 정리된다.)
await Task.WhenAny(Task.WhenAll(toPipe, toStdout), Task.Delay(500));
return 0;

static async Task PumpAsync(Stream from, Stream to, CancellationTokenSource cts)
{
    var buffer = new byte[16 * 1024];
    try
    {
        while (true)
        {
            int n = await from.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (n <= 0) break; // EOF
            await to.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);
            await to.FlushAsync(cts.Token).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    catch (ObjectDisposedException) { }
    finally
    {
        cts.Cancel();
    }
}
