using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace UartTerminal;

/// <summary>선택 가능한 표시 언어.</summary>
public enum AppLanguage
{
    Korean,
    English,
}

/// <summary>
/// 화면 문자열 제공자. XAML 에서는 <c>{loc:Str Menu.Terminal}</c> 로, 코드에서는 <c>Loc.S("...")</c> 로 쓴다.
///
/// <b>왜 인덱서 + INotifyPropertyChanged 인가</b>: 언어를 바꿀 때 앱을 재시작하면 시리얼 연결과
/// MCP 서버가 끊긴다. 인덱서 바인딩으로 두면 <see cref="SetLanguage"/> 가 한 번 알리는 것만으로
/// 화면의 모든 문자열이 다시 평가돼 <b>연결을 유지한 채 즉시</b> 바뀐다.
/// (테마는 브러시 인스턴스를 공유해 해결했지만 문자열은 값 타입이라 바인딩이 필요하다.)
///
/// 키가 없으면 <c>[키]</c> 를 그대로 돌려주어 누락이 화면에서 바로 보이게 한다(조용히 넘기지 않는다).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

    public static AppLanguage Language { get; private set; } = AppLanguage.Korean;

    /// <summary>언어가 바뀐 뒤 발생(코드에서 만든 문자열을 다시 계산해야 하는 곳용).</summary>
    public static event Action? Changed;

    private Loc() { }

    /// <summary>XAML 바인딩 진입점 — <c>{Binding [키]}</c>.</summary>
    public string this[string key] => S(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>현재 언어의 문자열. 없으면 <c>[키]</c>.</summary>
    public static string S(string key)
    {
        if (!Table.TryGetValue(key, out var pair))
        {
            DiagLog.Warn($"문자열 키 없음: {key}");
            return $"[{key}]";
        }
        string value = Language == AppLanguage.English ? pair.En : pair.Ko;
        return string.IsNullOrEmpty(value) ? pair.Ko : value;
    }

    /// <summary>언어 전환. 인덱서 하나를 알리면 모든 <c>{loc:Str}</c> 바인딩이 다시 평가된다.</summary>
    public static void SetLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language;
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(Binding.IndexerName));
        try { Changed?.Invoke(); } catch (Exception ex) { DiagLog.Exception("Loc.Changed", ex); }
        DiagLog.Info($"언어 적용: {language}");
    }

    /// <summary>번역 누락 점검용(테스트/진단). 값이 빈 키 목록.</summary>
    public static IReadOnlyList<string> MissingTranslations() =>
        Table.Where(e => string.IsNullOrEmpty(e.Value.En)).Select(e => e.Key).ToList();

    public static int Count => Table.Count;

    // ── 문자열 표 ────────────────────────────────────────────────────────────
    // 한 줄에 두 언어를 나란히 둔다 — 번역 누락과 불일치를 눈으로 바로 찾을 수 있다.
    // 메뉴 접근키(_X)는 언어별로 다를 수 있으나 최상위는 T·E·V·D·W·C·M·H 로 맞춰
    // 언어를 바꿔도 손가락이 기억하는 조합이 유지된다(전역 Alt+N/I/B/R 과 겹치지 않게).
    private static readonly Dictionary<string, (string Ko, string En)> Table = new(StringComparer.Ordinal)
    {
        // ── 메뉴: 터미널 ──
        ["Menu.Terminal"] = ("터미널(_T)", "_Terminal"),
        ["Menu.NewTab"] = ("새 연결(탭 추가)(_N)", "_New Connection (Tab)"),
        ["Menu.Reconnect"] = ("재연결(_R)…", "_Reconnect…"),
        ["Menu.Disconnect"] = ("연결 해제(_D)", "_Disconnect"),
        ["Menu.AutoReconnect"] = ("자동 재연결 (USB 재접속 시)(_A)", "_Auto-reconnect (on USB re-plug)"),
        ["Menu.Newline"] = ("개행(New-line)(_L)", "New-_line"),
        ["Menu.Newline.Rx"] = ("수신(Receive)", "Receive"),
        ["Menu.Newline.Tx"] = ("송신(Transmit)", "Transmit"),
        ["Menu.Newline.RxCrLf"] = ("CR+LF  — 개행=LF, CR=줄 처음으로(기본)", "CR+LF  — newline=LF, CR=column 0 (default)"),
        ["Menu.Newline.RxLf"] = ("LF  — 개행=LF, CR 무시", "LF  — newline=LF, ignore CR"),
        ["Menu.Newline.RxCr"] = ("CR  — 개행=CR, LF 무시", "CR  — newline=CR, ignore LF"),
        ["Menu.Newline.RxAuto"] = ("AUTO  — CR/LF/CR+LF 모두 개행", "AUTO  — CR / LF / CR+LF all break lines"),
        ["Menu.Newline.TxCr"] = ("CR  — esp_console 기본", "CR  — esp_console default"),
        ["Menu.Newline.TxCrLf"] = ("CR+LF", "CR+LF"),
        ["Menu.Newline.TxLf"] = ("LF", "LF"),
        ["Menu.SaveSession"] = ("현재 연결을 세션으로 저장(_S)…", "_Save Current Connection as Session…"),
        ["Menu.ManageSessions"] = ("세션 관리(_G)…", "Mana_ge Sessions…"),
        ["Menu.SaveLog"] = ("로그 저장(_V)…", "Sa_ve Log…"),
        ["Menu.CloseTab"] = ("탭 닫기(_W)", "Close Ta_b"),
        ["Menu.Exit"] = ("종료(_X)", "E_xit"),

        // ── 메뉴: 편집 ──
        ["Menu.Edit"] = ("편집(_E)", "_Edit"),
        ["Menu.Copy"] = ("복사(_C)", "_Copy"),
        ["Menu.Paste"] = ("붙여넣기(_P)", "_Paste"),
        ["Menu.Find"] = ("찾기(_F)…", "_Find…"),
        ["Menu.ClearScreen"] = ("화면 지우기(_S)", "Clear _Screen"),
        ["Menu.ClearBuffer"] = ("버퍼 지우기(_B)", "Clear _Buffer"),
        ["Gesture.Paste"] = ("Shift+Ins / 우클릭", "Shift+Ins / right-click"),

        // ── 메뉴: 보기 ──
        ["Menu.View"] = ("보기(_V)", "_View"),
        ["Menu.FontLarger"] = ("폰트 크게(_I)", "Font _Larger"),
        ["Menu.FontSmaller"] = ("폰트 작게(_O)", "Font Small_er"),
        ["Gesture.FontLarger"] = ("Ctrl++ / Ctrl+휠", "Ctrl++ / Ctrl+Wheel"),
        ["Gesture.FontSmaller"] = ("Ctrl+- / Ctrl+휠", "Ctrl+- / Ctrl+Wheel"),
        ["Menu.Timestamps"] = ("타임스탬프 표시(_T)", "Show _Timestamps"),
        ["Menu.Theme"] = ("테마(_M)", "The_me"),
        ["Menu.Theme.Dark"] = ("다크", "Dark"),
        ["Menu.Theme.Light"] = ("라이트", "Light"),
        ["Menu.Language"] = ("언어(_G)", "Lan_guage"),
        ["Menu.Language.Korean"] = ("한국어", "한국어 (Korean)"),
        ["Menu.Language.English"] = ("English", "English"),
        ["Menu.ScrollEnd"] = ("맨 아래로(_E)", "Scroll to _End"),

        // ── 메뉴: 보드 ──
        ["Menu.Board"] = ("보드(_D)", "Boar_d"),
        ["Menu.HardReset"] = ("하드웨어 리셋 (EN 펄스)(_R)", "Hardware _Reset (EN pulse)"),
        ["Menu.Bootloader"] = ("부트로더 모드 진입 (IO0=LOW)(_L)", "Enter Boot_loader (IO0=LOW)"),
        ["Menu.ResetOnOpen"] = ("열 때 보드 리셋 (이 탭 · 세션에 저장)(_O)", "Reset Board _on Open (this tab · saved in session)"),
        ["Menu.Flash"] = ("펌웨어 플래시(zip)(_F)…", "_Flash Firmware (zip)…"),

        // ── 메뉴: 창 ──
        ["Menu.Window"] = ("창(_W)", "_Window"),
        ["Menu.Detach"] = ("새 창으로 분리(_D)", "_Detach to New Window"),
        ["Menu.Merge"] = ("메인 창으로 합치기(_M)", "_Merge into Main Window"),
        ["Menu.SplitSingle"] = ("단일 화면(_1)", "Single Pane (_1)"),
        ["Menu.SplitV"] = ("좌우 분할(_2)", "Split Left/Right (_2)"),
        ["Menu.SplitH"] = ("상하 분할(_3)", "Split Top/Bottom (_3)"),
        ["Menu.SplitGrid"] = ("격자 분할(_4)", "Grid Split (_4)"),
        ["Tip.SplitSingle"] = ("단일 화면", "Single pane"),
        ["Tip.SplitV"] = ("좌우 분할", "Split left/right"),
        ["Tip.SplitH"] = ("상하 분할", "Split top/bottom"),
        ["Tip.SplitGrid"] = ("격자 분할", "Grid split"),

        // ── 메뉴: 명령 ──
        ["Menu.Command"] = ("명령(_C)", "_Command"),
        ["Menu.CommandBar"] = ("명령 바 표시(_S)", "_Show Command Bar"),
        ["Menu.SaveCommand"] = ("현재 입력을 명령으로 저장(_A)", "S_ave Current Input as Command"),
        ["Menu.EditCommands"] = ("저장 명령 편집(_E)…", "_Edit Saved Commands…"),

        // ── 메뉴: MCP ──
        ["Menu.Mcp"] = ("MCP(_M)", "_MCP"),
        ["Menu.McpEnabled"] = ("MCP 서버 활성화(_E)", "_Enable MCP Server"),
        ["Menu.McpReadOnly"] = ("AI 읽기 전용 (TX·제어선 차단)(_R)", "AI _Read-only (block TX & control lines)"),
        ["Menu.McpCopyCmd"] = ("등록 명령 복사 (claude mcp add)(_C)", "_Copy Registration Command (claude mcp add)"),

        // ── 메뉴: 도움말 ──
        ["Menu.Help"] = ("도움말(_H)", "_Help"),
        ["Menu.DiagCapture"] = ("진단 캡처 (RX/TX → diag.log)(_D)", "_Diagnostic Capture (RX/TX → diag.log)"),
        ["Menu.About"] = ("UartTerminal 정보(_A)", "_About UartTerminal"),

        // ── 상태바 / 탭 ──
        ["Status.Starting"] = ("시작 중…", "Starting…"),
        ["Status.McpOff"] = ("MCP: 꺼짐", "MCP: off"),
        ["Tip.CloseTab"] = ("탭 닫기", "Close tab"),
    };
}

/// <summary>
/// XAML 문자열 확장 — <c>Text="{loc:Str Status.Starting}"</c>.
/// 내부적으로 <see cref="Loc"/> 인덱서 바인딩을 만들기 때문에 언어를 바꾸면 즉시 갱신된다.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public StrExtension() { }
    public StrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Current,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
