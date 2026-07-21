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

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Exception(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

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
