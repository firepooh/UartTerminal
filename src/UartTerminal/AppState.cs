using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UartTerminal.Core.Terminal;

namespace UartTerminal;

/// <summary>
/// 설정 다이얼로그와는 별개의 최소 지속 상태(README §6 Q3): 마지막 포트/창 위치·크기/폰트 크기.
/// %APPDATA%\UartTerminal\state.json 에 원자적으로 저장.
/// </summary>
public sealed class AppState
{
    public string? LastPort { get; set; }

    /// <summary>마지막으로 선택한 통신 속도(bps). 포트 선택 다이얼로그의 기본값으로 쓰인다(README §2).</summary>
    public int LastBaud { get; set; } = 115200;

    public double FontSize { get; set; } = 14.0;

    /// <summary>USB 재접속(장치 분리) 시 같은 포트로 자동 재연결할지. 기본 켬.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>저장 명령 칩 바를 표시할지(Alt+B). 모든 창/탭에 공통 적용되는 전역 설정.</summary>
    public bool ShowCommandBar { get; set; } = true;

    /// <summary>진단 캡처(RX/TX 덤프 → diag.log). 문제 추적용, 기본 꺼짐.</summary>
    public bool DiagCapture { get; set; }

    /// <summary>라인별 수신 타임스탬프 표시. 모든 창/탭 공통 전역 설정, 기본 꺼짐.</summary>
    public bool ShowTimestamps { get; set; }

    /// <summary>
    /// 포트를 열 때 EN 펄스로 보드를 리셋할지(ESP32 devkit 의 DTR/RTS 자동 리셋 회로 이용). 기본 꺼짐.
    /// 켜면 연결/재연결마다 보드가 재부팅되어 부팅 로그를 처음부터 볼 수 있다.
    /// </summary>
    public bool ResetOnOpen { get; set; }

    /// <summary>수신 개행 규약. 기본 CR+LF(개행=LF, CR=줄 처음으로 — 진행바 덮어쓰기 유지).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReceiveNewline NewlineRx { get; set; } = ReceiveNewline.CrLf;

    /// <summary>송신 개행 규약. 기본 CR(esp_console/linenoise 규약).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TransmitNewline NewlineTx { get; set; } = TransmitNewline.Cr;

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
