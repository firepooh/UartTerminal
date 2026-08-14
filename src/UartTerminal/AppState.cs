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

    // ── 연속 로깅 다이얼로그의 기본값(마지막으로 고른 값) ──────────────────────

    /// <summary>
    /// 로그 파일에 줄마다 수신 시각(<c>[HH:mm:ss.fff]</c>)을 기록할지. 기본 꺼짐 —
    /// 끄면 파일이 수신 바이트 그대로라(diff/재현용) 원본성이 유지된다. 로깅 시작 시점 값으로 고정된다.
    /// </summary>
    public bool LogTimestamps { get; set; }

    /// <summary>true = 기존 파일 끝에 이어 쓰기. 기본은 새로 쓰기(실행마다 새 파일이 예측 가능).</summary>
    public bool LogAppend { get; set; }

    /// <summary>로깅 시작 시 현재 화면 버퍼(스크롤백)를 먼저 기록할지. 기본 꺼짐.</summary>
    public bool LogIncludeBuffer { get; set; }

    /// <summary>
    /// true = 수신 바이트 그대로 기록. 기본 false = <b>화면 그대로</b> —
    /// 원시 기록은 ANSI 이스케이프·NUL 이 그대로 남아 사람이 읽을 수 없다(실사용에서 확인).
    /// </summary>
    public bool LogRaw { get; set; }

    /// <summary>마지막 로그 파일 경로 — 이어 쓰기 워크플로에서 같은 파일을 바로 다시 고르게.</summary>
    public string? LastLogFile { get; set; }

    /// <summary>메시지 파서 정의 파일 경로. null = 기본(%APPDATA%\UartTerminal\parsers.json). 프로젝트마다 정의 파일을 골라 쓴다.</summary>
    public string? ParserFilePath { get; set; }

    /// <summary>파싱 패널에서 체크 해제한 메시지 키(예: "T12"). '해제' 를 저장해야 정의 파일에 새 키가 생겼을 때 기본으로 보인다.</summary>
    public List<string> ParseDisabledKeys { get; set; } = new();

    /// <summary>
    /// 포트를 열 때 EN 펄스로 보드를 리셋할지(ESP32 devkit 의 DTR/RTS 자동 리셋 회로 이용). 기본 꺼짐.
    /// 켜면 연결/재연결마다 보드가 재부팅되어 부팅 로그를 처음부터 볼 수 있다.
    /// </summary>
    public bool ResetOnOpen { get; set; }

    // 테마 설정은 없앴다(다크 단독). 옛 state.json 에 남아 있는 "theme" 키는
    // System.Text.Json 이 모르는 멤버로 무시하므로 마이그레이션이 필요 없다.

    /// <summary>표시 언어(한국어/English). 전역 설정 — 재시작 없이 즉시 적용된다.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppLanguage Language { get; set; } = AppLanguage.Korean;

    /// <summary>마지막으로 플래시한 펌웨어 패키지(zip) 경로. 반복 작업이 대부분이라 다음에 바로 불러온다.</summary>
    public string? LastFlashZip { get; set; }

    /// <summary>플래시 속도(bps). 기본 576000 — ESP32-S3 devkit 에서 안정적으로 쓰이는 값.</summary>
    public int FlashBaud { get; set; } = 576000;

    /// <summary>
    /// esptool 실행 파일 경로(직접 지정). 비면 자동 탐색(앱 번들 → ESP-IDF 설치본 → PATH).
    /// ESP-IDF 가 없는 PC 에서 standalone esptool 을 쓸 때 여기에 적는다.
    /// </summary>
    public string? EsptoolPath { get; set; }

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
