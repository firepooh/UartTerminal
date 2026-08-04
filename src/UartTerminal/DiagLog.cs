using System.IO;

namespace UartTerminal;

/// <summary>
/// 앱 자체 진단 로그(README §5). 시리얼 데이터 로그와 별개로 예외·포트 이벤트·재연결 시도를 기록해
/// "가끔 수신이 멈춘다" 류 문제를 추적한다. %LOCALAPPDATA%\UartTerminal\diag.log, 크기 초과 시 1회 롤링.
/// </summary>
public static class DiagLog
{
    private static readonly object Sync = new();
    private const long MaxBytes = 1_000_000;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UartTerminal");

    private static string FilePath => Path.Combine(Dir, "diag.log");

    /// <summary>진단 캡처(RX/TX 덤프) 활성 여부. [도움말] 메뉴 토글 + state.json 에 지속. 기본 꺼짐.</summary>
    public static volatile bool Capture;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    /// <summary>진단 캡처가 켜져 있을 때만 기록되는 상세 트레이스(RX/TX 등).</summary>
    public static void Trace(string msg) { if (Capture) Write("TRACE", msg); }

    public static void Exception(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>바이트를 사람이 읽는 형태로(ESC→\e, CR→\r, LF→\n, 기타 제어/비ASCII→\xNN, 나머지는 그대로).</summary>
    public static string Escape(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 2);
        foreach (byte b in data)
        {
            switch (b)
            {
                case 0x1B: sb.Append("\\e"); break;
                case 0x0D: sb.Append("\\r"); break;
                case 0x0A: sb.Append("\\n"); break;
                case 0x09: sb.Append("\\t"); break;
                default:
                    if (b < 0x20 || b >= 0x7F) sb.Append($"\\x{b:x2}");
                    else sb.Append((char)b);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Dir);
                string path = FilePath;
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        string bak = path + ".1";
                        File.Delete(bak);
                        File.Move(path, bak);
                    }
                }
                catch { /* 롤링 실패는 무시 */ }

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // 진단 로그 자체의 실패는 삼킨다(앱 동작에 영향 없어야 함)
        }
    }
}
