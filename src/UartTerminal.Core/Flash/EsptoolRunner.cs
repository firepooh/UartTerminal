using System.Diagnostics;
using System.Text;

namespace UartTerminal.Core.Flash;

/// <summary>esptool 실행 결과.</summary>
public sealed record EsptoolRunResult
{
    public bool Ok { get; init; }
    public int ExitCode { get; init; }
    public bool Canceled { get; init; }
    public string Output { get; init; } = "";
    public LocMessage? Error { get; init; }
}

/// <summary>
/// esptool 프로세스 실행기. 표준출력/오류를 <b>줄 단위로 흘려보내며</b> 진행률을 계산한다.
///
/// 두 가지가 중요하다:
///  - esptool 은 진행률을 TTY 에서 <c>\r</c> 로 덮어쓰고 리다이렉트 시에는 개행으로 찍는다.
///    어느 쪽이든 받도록 <c>\r</c>·<c>\n</c> 모두 줄 구분자로 취급한다.
///  - 취소는 프로세스를 죽이는 것이다(esptool 은 중단 신호를 받지 않는다). 죽인 뒤에는
///    호출자가 포트를 다시 열 수 있도록 반드시 반환한다.
/// </summary>
public sealed class EsptoolRunner
{
    private readonly string _exePath;

    public EsptoolRunner(string exePath) => _exePath = exePath;

    /// <summary>출력 한 줄(로그 창에 그대로 붙인다).</summary>
    public event Action<string>? Line;

    /// <summary>진행률 변경.</summary>
    public event Action<FlashProgress>? Progress;

    public async Task<EsptoolRunResult> RunAsync(IReadOnlyList<string> args, IReadOnlyList<long> fileSizes,
                                                 CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        var parser = new EsptoolProgressParser(fileSizes);
        var all = new StringBuilder();
        var gate = new object();

        void Emit(string line)
        {
            lock (gate)
            {
                all.AppendLine(line);
                try { Line?.Invoke(line); } catch { }
                if (parser.Feed(line, out var p))
                {
                    try { Progress?.Invoke(p); } catch { }
                }
            }
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return new EsptoolRunResult { Ok = false, ExitCode = -1, Error = LocMessage.Of("Flash.Err.CannotStart") };
        }
        catch (Exception ex)
        {
            return new EsptoolRunResult
            {
                Ok = false, ExitCode = -1,
                Error = LocMessage.Of("Flash.Err.StartFailed", ex.Message),
            };
        }

        var pump = Task.WhenAll(
            PumpAsync(proc.StandardOutput, Emit, ct),
            PumpAsync(proc.StandardError, Emit, ct));

        bool canceled = false;
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(3000); } catch { }
        }

        // 남은 출력까지 비운다(취소 시에도 마지막 줄들을 로그에 남긴다).
        try { await pump.ConfigureAwait(false); } catch { }

        int exit = -1;
        try { exit = proc.ExitCode; } catch { }

        return new EsptoolRunResult
        {
            Ok = !canceled && exit == 0,
            ExitCode = exit,
            Canceled = canceled,
            Output = all.ToString(),
            Error = canceled ? LocMessage.Of("Flash.Err.Canceled")
                  : exit == 0 ? null : LocMessage.Of("Flash.Err.ExitCode", exit),
        };
    }

    /// <summary>스트림을 읽어 <c>\r</c>/<c>\n</c> 로 잘라 줄 단위로 넘긴다.</summary>
    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        var buf = new char[4096];
        var sb = new StringBuilder(256);
        try
        {
            while (true)
            {
                int n = await reader.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;

                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c is '\r' or '\n')
                    {
                        if (sb.Length > 0)
                        {
                            onLine(sb.ToString());
                            sb.Clear();
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 취소는 정상 종료 경로
        }
        catch
        {
            // 프로세스가 죽으면서 스트림이 끊기는 것은 무시
        }

        if (sb.Length > 0) onLine(sb.ToString());
    }

    /// <summary><c>esptool version</c> 을 짧게 실행해 버전 문자열을 얻는다(탐색용).</summary>
    public static string? ProbeVersion(string exePath, int timeoutMs = 5000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return output;
        }
        catch
        {
            return null;
        }
    }
}
