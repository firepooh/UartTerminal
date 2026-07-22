using System.IO;
using System.Text.Json;

namespace UartTerminal;

/// <summary>
/// 설정 다이얼로그와는 별개의 최소 지속 상태(README §6 Q3): 마지막 포트/창 위치·크기/폰트 크기.
/// %APPDATA%\UartTerminal\state.json 에 원자적으로 저장.
/// </summary>
public sealed class AppState
{
    public string? LastPort { get; set; }
    public double FontSize { get; set; } = 14.0;

    /// <summary>USB 재접속(장치 분리) 시 같은 포트로 자동 재연결할지. 기본 켬.</summary>
    public bool AutoReconnect { get; set; } = true;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UartTerminal");

    private static string FilePath => Path.Combine(Dir, "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            DiagLog.Warn($"AppState.Load 실패: {ex.Message}");
        }
        return new AppState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            // 원자적 교체
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            DiagLog.Warn($"AppState.Save 실패: {ex.Message}");
        }
    }
}
