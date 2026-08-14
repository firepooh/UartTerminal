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

    /// <summary>
    /// 코드로 만드는 컨트롤용 바인딩. <c>Header = Loc.S(key)</c> 처럼 <b>대입</b>하면 그 시점 언어로
    /// 굳어 버리므로(탭 컨텍스트 메뉴·툴팁에서 실제로 그랬다), <c>SetBinding</c> 으로 걸어야
    /// 언어 전환을 따라온다. XAML 의 <c>{loc:Str …}</c> 와 같은 바인딩을 만든다.
    /// </summary>
    public static Binding Bind(string key) =>
        new($"[{key}]") { Source = Current, Mode = BindingMode.OneWay };

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


    /// <summary>서식 문자열에 인자를 채운다(<c>{0}</c> 자리). 인자 개수가 어긋나도 예외 없이 원문을 돌려준다.</summary>
    public static string F(string key, params object?[] args)
    {
        string template = S(key);
        if (args.Length == 0) return template;
        try { return string.Format(template, args); }
        catch (FormatException)
        {
            DiagLog.Warn($"문자열 서식 불일치: {key}");
            return template;
        }
    }

    /// <summary>Core 가 돌려준 메시지(키 + 인자)를 현재 언어 문장으로 조립한다.</summary>
    public static string Format(UartTerminal.Core.LocMessage message) =>
        F(message.Key, message.Args.Cast<object?>().ToArray());

    /// <summary>
    /// null 을 그대로 흘리는 조립(스토어의 <c>LastError</c> 용).
    /// 호출부가 <c>Loc.FormatOrNull(store.LastError) ?? Loc.S("…폴백")</c> 로 쓸 수 있게 한다.
    /// </summary>
    public static string? FormatOrNull(UartTerminal.Core.LocMessage? message) =>
        message is null ? null : Format(message);

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
        ["Menu.SaveLog"] = ("화면 버퍼 저장(_V)…", "Sa_ve Screen Buffer…"),
        // 연속 로깅(수신 원시 바이트 → 파일, 상한 없음). 옵션은 시작 다이얼로그(LogDialog)에서 —
        // 메뉴는 TeraTerm 처럼 '로깅' 하나만 둔다.
        ["Menu.LogStart"] = ("로깅(_O)…", "L_ogging…"),
        ["Menu.LogStop"] = ("로깅 정지(_O)", "St_op Logging"),
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
        ["Status.McpOn"] = ("MCP: 켜짐 ({0})", "MCP: on ({0})"),
        ["Status.McpOnReadOnly"] = ("MCP: 켜짐 (읽기 전용, {0})", "MCP: on (read-only, {0})"),
        ["Tip.CloseTab"] = ("탭 닫기", "Close tab"),
        // 탭 우클릭 메뉴. 메뉴바와 같은 동작이라 접근키(_) 없는 짧은 문구를 따로 둔다.
        ["Ctx.Detach"] = ("새 창으로 분리", "Detach to New Window"),
        ["Ctx.Merge"] = ("메인 창으로 합치기", "Merge into Main Window"),
        ["Ctx.CloseTab"] = ("탭 닫기", "Close Tab"),

        // ── 대화상자 ──
        ["Common.Ok"] = ("확인", "OK"),
        ["Common.Cancel"] = ("취소", "Cancel"),
        ["Common.Save"] = ("저장", "Save"),
        ["Common.Delete"] = ("삭제", "Delete"),
        ["Common.Edit"] = ("편집", "Edit"),
        ["Common.Add"] = ("추가", "Add"),
        ["Common.Rename"] = ("이름", "Rename"),
        ["Common.Refresh"] = ("새로고침", "Refresh"),
        ["Common.Connect"] = ("연결", "Connect"),
        ["Common.Close"] = ("닫기", "Close"),
        ["Common.Browse"] = ("찾기…", "Browse…"),
        ["Common.OpenFolder"] = ("폴더 열기", "Open Folder"),
        ["Common.Clear"] = ("지우기", "Clear"),
        ["Common.Name"] = ("이름", "Name"),
        ["Common.Port"] = ("포트", "Port"),
        ["Common.Baud"] = ("속도", "Speed"),
        ["Common.BaudBps"] = ("속도(bps)", "Speed (bps)"),
        ["Common.CommandGroup"] = ("명령 그룹", "Command Group"),
        ["Common.Up"] = ("위로", "Move up"),
        ["Common.Down"] = ("아래로", "Move down"),
        ["Port.Title"] = ("연결 — UartTerminal", "Connect — UartTerminal"),
        ["Port.Sessions"] = ("저장된 세션 (더블클릭하면 연결)", "Saved sessions (double-click to connect)"),
        ["Port.Detected"] = ("감지된 포트", "Detected ports"),
        ["Port.Hint"] = ("세션 없이 포트만 골라 바로 연결해도 됩니다.", "You can also just pick a port and connect."),
        ["Port.ResetOnOpen"] = ("열 때 보드 리셋 (EN 펄스 — 부팅 로그를 처음부터 보기)", "Reset board on open (EN pulse — see boot log from the start)"),
        ["Port.Fixed"] = ("나머지는 고정: 8비트 · 패리티 없음 · 스톱 1 · 흐름제어 없음 · 오픈 시 DTR/RTS 해제", "Fixed: 8 data bits · no parity · 1 stop bit · no flow control · DTR/RTS deasserted on open"),
        ["About.Title"] = ("UartTerminal 정보", "About UartTerminal"),
        ["About.Summary"] = ("ESP-IDF/ESP32 개발용 시리얼 터미널 — 컬러 로그, 창 크기에 맞춘 reflow, 화면 분할, USB 자동 재연결, AI(MCP) 포트 공유.", "Serial terminal for ESP-IDF/ESP32 development — color logs, reflow on resize, split panes, USB auto-reconnect, AI (MCP) port sharing."),
        ["Doc.GroupTip"] = ("명령 그룹(프로젝트) 선택", "Select command group (project)"),
        ["Doc.SaveChip"] = ("+ 저장", "+ Save"),
        ["Doc.SaveChipTip"] = ("현재 입력창 내용을 명령으로 저장", "Save current input as a command"),
        ["Doc.EditChipTip"] = ("저장 명령 편집", "Edit saved commands"),
        ["Doc.FindPrevTip"] = ("이전 (Shift+Enter)", "Previous (Shift+Enter)"),
        ["Doc.FindNextTip"] = ("다음 (Enter)", "Next (Enter)"),
        ["Doc.FindCloseTip"] = ("닫기 (Esc)", "Close (Esc)"),
        ["Sess.Title"] = ("세션 관리 — UartTerminal", "Manage Sessions — UartTerminal"),
        ["Sess.Intro"] = ("저장된 접속 프로필입니다. 이름·포트·속도·열 때 동작(리셋·MCP)과 연결된 명령 그룹을 한눈에 확인하고 편집할 수 있습니다. (접속은 [터미널 > 재연결] 또는 새 탭에서 세션을 더블클릭)", "Saved connection profiles. Review and edit name, port, speed, on-open actions (reset, MCP) and the linked command group at a glance. (To connect: [Terminal > Reconnect] or double-click a session in a new tab.)"),
        ["Sess.NewlineCol"] = ("개행 ↓수신 ↑송신", "New-line ↓Rx ↑Tx"),
        ["Sess.OnOpen"] = ("열 때", "On open"),
        ["Sess.OnOpenNone"] = ("—", "—"),
        ["Sess.Reset"] = ("리셋", "Reset"),
        ["Sess.ResetTip"] = ("이 세션으로 접속할 때 EN 펄스로 보드를 리셋(부팅 로그를 처음부터)", "Reset the board with an EN pulse when connecting with this session (boot log from the start)"),
        ["Sess.Mcp"] = ("MCP", "MCP"),
        ["Sess.LogFolder"] = ("로그 폴더 (비우면 마지막에 쓴 폴더)", "Log folder (empty = last folder used)"),
        ["Sess.LogFolderTip"] = ("이 세션으로 접속한 탭에서 로깅을 시작할 때 미리 채울 폴더. 파일 이름은 '세션_포트_YYMMDD_HHMMSS.log' 규칙으로 매번 새로 만든다", "Folder pre-filled when logging starts in a tab opened with this session. The file name is generated each time as 'session_port_YYMMDD_HHMMSS.log'"),
        ["Sess.PickLogFolder"] = ("로그 폴더 선택", "Pick log folder"),
        ["Sess.McpTip"] = ("이 세션으로 접속할 때 이 탭의 MCP 서버를 자동으로 켠다(AI 도구 연결 — 포트마다 파이프가 다르다)", "Start this tab's MCP server automatically when connecting with this session (for AI tools — each port has its own pipe)"),
        ["Sess.NewlineRx"] = ("개행 — 수신(Receive)", "New-line — Receive"),
        ["Sess.NewlineTx"] = ("송신(Transmit)", "Transmit"),
        ["Sess.RxTip"] = ("이 세션으로 접속할 때 쓸 수신 개행 규약((기본)이면 현재 설정을 그대로 사용)", "Receive new-line convention for this session ((default) keeps the current setting)"),
        ["Sess.TxTip"] = ("Enter·명령 칩·붙여넣기·AI 전송에 쓸 송신 개행 규약", "Transmit new-line used by Enter, command chips, paste and AI sends"),
        ["Flash.Title"] = ("펌웨어 플래시 — UartTerminal", "Flash Firmware — UartTerminal"),
        ["Flash.Intro"] = ("빌드 산출물 zip 을 고르면 오프셋·칩을 자동으로 읽어 목록을 채웁니다. 시작하면 이 탭의 포트를 잠시 양보하고, 끝나면 다시 연결합니다.", "Pick a build-output zip and the offsets and chip are filled in automatically. Starting releases this tab's port temporarily and reconnects when done."),
        ["Flash.Package"] = ("펌웨어 패키지(zip)", "Firmware package (zip)"),
        ["Flash.Chip"] = ("칩", "Chip"),
        ["Flash.ChipTip"] = ("기본은 패키지의 bootloader 헤더에서 읽은 칩입니다. 다르게 플래시하고 싶을 때만 바꾸세요.", "Defaults to the chip read from the package's bootloader header. Change only if you must flash a different one."),
        ["Flash.Keep"] = ("바이너리 그대로", "Keep binary as-is"),
        ["Flash.KeepTip"] = ("Flash Download Tool 의 DoNotChgBin 과 같은 동작 — 빌드 때 정한 SPI 설정(헤더)을 덮어쓰지 않습니다.", "Same as Flash Download Tool's DoNotChgBin — does not overwrite the SPI settings (header) chosen at build time."),
        ["Flash.Reconnect"] = ("완료 후 재연결", "Reconnect when done"),
        ["Flash.Offset"] = ("오프셋", "Offset"),
        ["Flash.Size"] = ("크기", "Size"),
        ["Flash.Role"] = ("역할", "Role"),
        ["Flash.File"] = ("파일", "File"),
        ["Flash.Note"] = ("비고", "Notes"),
        ["Flash.Idle"] = ("대기", "Idle"),
        ["Flash.Tab.Package"] = ("펌웨어 패키지", "Firmware Package"),
        ["Flash.Tab.Region"] = ("부분 플래시", "Partial Flash"),
        ["Flash.RegionIntro"] = ("바이너리 하나를 지정한 주소에 플래시합니다(칩은 자동 감지). 소스는 BIN 파일·zip(풀어서 선택)·http 링크(내려받아 처리) 모두 됩니다.",
                                 "Flashes one binary to the given address (chip auto-detected). The source can be a BIN file, a zip (extracted, then pick) or an http link (downloaded first)."),
        ["Flash.RegionSource"] = ("소스 — BIN·zip 파일 또는 http 링크", "Source — BIN/zip file or http link"),
        ["Flash.RegionSourceTip"] = ("파일 경로나 http(s) 링크를 넣고 [불러오기] 또는 Enter", "Enter a file path or an http(s) link, then [Load] or Enter"),
        ["Flash.RegionSourcePick"] = ("BIN 또는 zip 선택", "Pick a BIN or zip"),
        ["Flash.RegionSourceFilter"] = ("펌웨어 이미지 (*.bin;*.zip)|*.bin;*.zip|모든 파일 (*.*)|*.*",
                                        "Firmware images (*.bin;*.zip)|*.bin;*.zip|All files (*.*)|*.*"),
        ["Flash.RegionLoad"] = ("불러오기", "Load"),
        ["Flash.RegionAddr"] = ("주소 (CANDB — 0xD90000)", "Address (CANDB — 0xD90000)"),
        ["Flash.RegionAddrTip"] = ("플래시 주소 — hex(0xD90000) 또는 십진", "Flash address — hex (0xD90000) or decimal"),
        ["Flash.RegionBin"] = ("바이너리", "Binary"),
        ["Flash.Log.Downloading"] = ("내려받는 중: {0}", "Downloading: {0}"),
        ["Flash.Log.Downloaded"] = ("내려받음: {0} ({1:N1} KB)", "Downloaded: {0} ({1:N1} KB)"),
        ["Flash.Log.PickBin"] = ("BIN 이 {0}개 나왔습니다 — 목록에서 플래시할 파일을 고르세요.", "{0} BIN files found — pick the one to flash."),
        ["Flash.Msg.SourceMissing"] = ("파일이 없습니다: {0}", "File not found: {0}"),
        ["Flash.Msg.SourceFailed"] = ("소스를 불러오지 못했습니다: {0}", "Could not load the source: {0}"),
        ["Flash.Msg.NoBin"] = ("압축 안에 BIN 파일이 없습니다.", "No BIN file inside the archive."),
        ["Flash.RegionLoading"] = ("불러오는 중…", "Loading…"),
        ["Flash.RegionNeedLoad"] = ("[불러오기]를 누르면 BIN 이 준비됩니다", "Press [Load] to prepare the BIN"),
        ["Flash.Msg.RegionConfirm"] = ("{0} 에서 {1} ({2} KB) 을(를)\n주소 {3} 에 씁니다 (속도 {4}bps).\n\n주소가 맞는지 확인하세요 — 잘못된 주소에 쓰면 장비가 동작하지 않을 수 있습니다.",
                                       "Writing {1} ({2} KB) via {0}\nto address {3} at {4} bps.\n\nVerify the address — writing to a wrong address can brick the device."),
        ["Flash.CheckingTool"] = ("esptool 확인 중…", "Checking esptool…"),
        ["Flash.Start"] = ("플래시 시작", "Start Flash"),
        ["Flash.Stop"] = ("중지", "Stop"),
        ["Cmd.Title"] = ("저장 명령 편집 — UartTerminal", "Edit Saved Commands — UartTerminal"),
        ["Cmd.Intro"] = ("그룹(프로젝트)별로 명령을 나눠 저장하고, 세션에 그룹을 연결하면 접속 시 자동 선택됩니다. 폴더를 만들어 하위 명령(예: reset → sw/hw/wdt)을 고르게 할 수 있고, 목록은 드래그로 순서를 바꿉니다.", "Store commands per group (project); link a group to a session and it is selected automatically on connect. Create a folder to choose among sub-commands (e.g. reset → sw/hw/wdt), and drag items to reorder."),
        ["Cmd.Groups"] = ("그룹 (프로젝트)", "Groups (projects)"),
        ["Cmd.Commands"] = ("명령", "Commands"),
        ["Cmd.AddCommand"] = ("명령 추가", "Add command"),
        ["Cmd.AddFolder"] = ("폴더 추가", "Add folder"),
        ["Cmd.AddChild"] = ("하위 명령 추가", "Add sub-command"),
        ["Cmd.AddChildTip"] = ("선택한 폴더에 하위 명령 추가", "Add a sub-command to the selected folder"),
        ["Cmd.NameLabel"] = ("이름 (칩/메뉴에 표시)", "Name (shown on chip/menu)"),
        ["Cmd.TextLabel"] = ("전송할 문자열 (한 줄, 전송 시 CR 이 붙습니다)", "Text to send (single line; a CR is appended)"),
        ["Cmd.Confirm"] = ("전송 전 확인 (restart · erase 처럼 위험한 명령)", "Confirm before sending (for risky commands like restart / erase)"),
        ["Cmd.FolderHint"] = ("폴더입니다 — 전송 문자열이 없고, 클릭하면 하위 명령을 고르는 메뉴가 열립니다. [하위 명령 추가]로 항목을 넣으세요.", "This is a folder — it has no text to send; clicking it opens a menu of sub-commands. Use [Add sub-command] to add entries."),
        ["Cmd.SecretWarn"] = ("비밀번호 같은 민감정보는 저장하지 마세요 (commands.json 은 평문입니다).", "Do not store secrets such as passwords (commands.json is plain text)."),

        // ── 플래시(Core 가 키로 돌려주는 메시지 + 화면 문구) ──
        ["Flash.Phase.Preparing"] = ("준비 중", "Preparing"),
        ["Flash.Phase.Connecting"] = ("연결 중", "Connecting"),
        ["Flash.Phase.ChipCheck"] = ("칩 확인", "Chip check"),
        ["Flash.Phase.Stub"] = ("스텁 로딩", "Loading stub"),
        ["Flash.Phase.Baud"] = ("속도 변경", "Changing baud"),
        ["Flash.Phase.Configure"] = ("플래시 설정", "Configuring flash"),
        ["Flash.Phase.Erasing"] = ("지우는 중", "Erasing"),
        ["Flash.Phase.Writing"] = ("쓰는 중", "Writing"),
        ["Flash.Phase.Verifying"] = ("검증/마무리", "Verifying"),
        ["Flash.Phase.Verified"] = ("검증됨", "Verified"),
        ["Flash.Phase.Finishing"] = ("마무리", "Finishing"),
        ["Flash.Phase.Resetting"] = ("리셋", "Resetting"),
        ["Flash.Phase.Done"] = ("완료", "Done"),
        ["Flash.Phase.Stopped"] = ("중지됨", "Stopped"),
        ["Flash.Phase.Failed"] = ("실패", "Failed"),
        ["Flash.Phase.Releasing"] = ("포트 양보 중…", "Releasing port…"),
        ["Flash.Warn.UnknownLocation"] = ("{0} 은(는) 쓸 위치를 알 수 없어 목록에 넣지 않았습니다(필요하면 직접 추가).", "{0} was left out — its flash location is unknown (add it manually if needed)."),
        ["Flash.Warn.ChipMismatch"] = ("선택한 칩({0})이 패키지에서 판별된 칩({1})과 다릅니다 — 부트로더 오프셋이 맞지 않으면 부팅되지 않습니다.", "Selected chip ({0}) differs from the chip detected in the package ({1}) — if the bootloader offset does not match, the board will not boot."),
        ["Flash.Warn.ChipUnknown"] = ("칩을 판별하지 못했습니다 — 칩을 직접 고르거나 연결된 보드에서 감지하세요.", "Could not determine the chip — pick one manually or detect it from the connected board."),
        ["Flash.Warn.BootloaderOffset"] = ("{0} 의 부트로더 오프셋은 {1} 인데 패키지는 {2} 입니다 — 칩이나 패키지가 맞지 않을 수 있습니다.", "{0} expects the bootloader at {1} but the package uses {2} — the chip or the package may not match."),
        ["Flash.Warn.ArgsReadFailed"] = ("{0} 을(를) 읽지 못했습니다: {1}", "Could not read {0}: {1}"),
        ["Flash.Warn.NoArgsFile"] = ("flash_project_args / flasher_args.json 이 없어 파일명 관례로 오프셋을 추정했습니다 — 값을 확인하세요.", "No flash_project_args / flasher_args.json — offsets were guessed from file-name conventions; please verify them."),
        ["Flash.Warn.ArgsRenamed"] = ("{0}: args 는 {1} 을 가리키지만 패키지에 없어 {2} 을 씁니다.", "{0}: args points to {1}, which is not in the package — using {2} instead."),
        ["Flash.Warn.ArgsCandidates"] = ("{0}: args 는 {1} 을 가리키지만 패키지에 없습니다. 후보 {2}개({3}) 중 {4} 을 기본 선택했습니다.", "{0}: args points to {1}, which is not in the package. {2} candidates ({3}) — {4} selected by default."),
        ["Flash.Warn.FileMissing"] = ("{0}: {1} 을(를) 패키지에서 찾지 못했습니다.", "{0}: {1} was not found in the package."),
        ["Flash.Err.NoFiles"] = ("플래시할 파일을 찾지 못했습니다(flash_project_args / flasher_args.json 도 없음).", "No files to flash (and no flash_project_args / flasher_args.json)."),
        ["Flash.Err.DuplicateOffset"] = ("오프셋 {0} 에 파일이 {1}개 잡혔습니다 ({2}) — 하나만 남기세요.", "{1} files map to offset {0} ({2}) — keep only one."),
        ["Flash.Err.EmptyFile"] = ("{0} 크기가 0입니다.", "{0} is empty (0 bytes)."),
        ["Flash.Note.Missing"] = ("패키지에 파일이 없습니다", "not in package"),
        ["Flash.Note.UncheckedConfig"] = ("기본 해제 — 구성이 바뀌면 체크", "off by default — check if layout changed"),
        ["Flash.Note.UncheckedData"] = ("기본 해제(데이터 보호)", "off by default (protects device data)"),
        ["Flash.Note.Unchecked"] = ("기본 해제", "off by default"),
        ["Flash.Note.Replaced"] = ("args 의 {0} 을(를) 찾지 못해 {1} 으로 대체", "{0} from args not found — using {1}"),
        ["Flash.Note.Candidates"] = ("후보 {0}개 — 확인 필요", "{0} candidates — please verify"),
        ["Flash.Note.EstimatedOffset"] = ("오프셋 추정값 — 확인 필요", "offset is a guess — please verify"),
        ["Flash.Log.Extract"] = ("패키지 해제: {0}", "Extracting package: {0}"),
        ["Flash.Log.ReleaseRequest"] = ("{0} 양보 요청", "Requesting release of {0}"),
        ["Flash.Log.ReleaseFailed"] = ("포트를 양보하지 못했습니다 — 중단합니다.", "Could not release the port — aborting."),
        ["Flash.Log.Reconnecting"] = ("{0} 재연결…", "Reconnecting {0}…"),
        ["Flash.Log.Reconnected"] = ("재연결됨.", "Reconnected."),
        ["Flash.Log.ReconnectFailed"] = ("재연결 실패 — [터미널 > 재연결](Alt+N)로 다시 시도하세요.", "Reconnect failed — try [Terminal > Reconnect] (Alt+N)."),
        ["Flash.Log.StillReleased"] = ("포트는 양보된 상태입니다 — 필요하면 Alt+N 으로 재연결하세요.", "The port is still released — press Alt+N to reconnect when ready."),
        ["Flash.Log.Done"] = ("플래시 완료.", "Flash complete."),
        ["Flash.Log.Failed"] = ("실패", "Failed"),
        ["Flash.Log.Canceled"] = ("취소되었습니다.", "Canceled."),
        ["Flash.Log.StopRequested"] = ("중지 요청…", "Stop requested…"),
        ["Flash.Log.Error"] = ("오류: {0}", "Error: {0}"),
        ["Flash.Msg.ReleaseFailedBody"] = ("포트를 양보하지 못해 플래시를 시작할 수 없습니다.", "Cannot start flashing because the port could not be released."),
        ["Flash.Msg.DuplicateOffset"] = ("오프셋 {0} 에 파일이 여러 개 선택됐습니다. 하나만 남기세요.", "Multiple files are selected for offset {0}. Keep only one."),
        ["Flash.Msg.ConfirmTitle"] = ("펌웨어 플래시", "Flash Firmware"),
        ["Flash.Msg.Confirm"] = ("{0} 에 {1}개 파일({2} MB)을 씁니다.\n칩: {3} · 속도: {4}\n\n진행하는 동안 이 탭의 연결이 잠시 끊깁니다. 계속할까요?", "About to write {1} file(s) ({2} MB) to {0}.\nChip: {3} · Speed: {4}\n\nThis tab's connection will drop while flashing. Continue?"),
        ["Flash.Chip.Auto"] = ("자동", "auto"),
        ["Flash.Chip.AutoDetected"] = ("자동 — {0}", "Auto — {0}"),
        ["Flash.Chip.AutoFailed"] = ("자동 (판별 실패 — 직접 고르세요)", "Auto (detection failed — pick manually)"),
        ["Flash.Chip.Tip"] = ("판별 근거: {0}\nesptool --chip {1}", "Detected via: {0}\nesptool --chip {1}"),
        ["Flash.Chip.TipUnknown"] = ("패키지에서 칩을 판별하지 못했습니다 — 직접 고르세요.", "Could not determine the chip from the package — pick one manually."),
        ["Flash.Tool.NotFound"] = ("esptool 을 찾지 못했습니다 — ESP-IDF 를 설치하거나 esptool 실행 파일을 지정하세요.", "esptool not found — install ESP-IDF or point to an esptool executable."),
        ["Flash.Tool.NotFoundHelp"] = ("esptool 실행 파일이 없어 플래시할 수 없습니다.\n· ESP-IDF 가 설치된 PC 라면 자동으로 찾습니다.\n· 아니면 esptool 공식 릴리스(standalone)를 받아 앱 폴더의 tools\\esptool\\ 에 두거나 state.json 의 esptoolPath 에 경로를 적으세요.", "Cannot flash without an esptool executable.\n· On a PC with ESP-IDF installed it is found automatically.\n· Otherwise download the official standalone esptool release and place it in tools\\esptool\\ next to the app, or set esptoolPath in state.json."),
        ["Flash.Msg.OpenFailed"] = ("패키지를 열지 못했습니다: {0}", "Could not open the package: {0}"),
        ["Flash.Msg.ExtractMissing"] = ("해제된 파일을 찾을 수 없습니다: {0}", "Extracted file not found: {0}"),
        ["Flash.Msg.NoPort"] = ("먼저 포트에 연결하세요.\n플래시는 현재 탭의 포트를 사용합니다.", "Connect to a port first.\nFlashing uses the port of the current tab."),
        ["Flash.Status.NoPort"] = ("플래시할 포트가 없습니다 — 먼저 연결하세요(Alt+N)", "No port to flash — connect first (Alt+N)"),
        ["Flash.Prefix.Warn"] = ("경고: {0}", "Warning: {0}"),
        ["Flash.Prefix.Err"] = ("오류: {0}", "Error: {0}"),
        ["Flash.NoConnection"] = ("(연결 없음)", "(not connected)"),
        ["Flash.FileMissingCell"] = ("(패키지에 없음)", "(not in package)"),
        ["Flash.Chip.AutoGeneric"] = ("자동 (패키지에서 판별)", "Auto (detect from package)"),
        ["Flash.PickTitle"] = ("펌웨어 패키지 선택", "Select firmware package"),
        ["Flash.PickFilter"] = ("펌웨어 패키지 (*.zip)|*.zip|모든 파일 (*.*)|*.*", "Firmware package (*.zip)|*.zip|All files (*.*)|*.*"),

        // ── 상태바 · 확인창(App 계층) ──
        ["Conn.Connected"] = ("연결됨: {0}", "Connected: {0}"),
        ["Conn.InUse"] = ("{0} 사용 중(다른 프로그램/창)", "{0} is in use (another program or window)"),
        ["Conn.OpenFailed"] = ("연결 실패: {0}", "Connection failed: {0}"),
        ["Conn.Disconnected"] = ("연결 해제됨", "Disconnected"),
        ["Conn.ClosedError"] = ("연결 종료(오류)", "Connection closed (error)"),
        ["Conn.DeviceRemoved"] = ("장치 분리됨 — Alt+N 또는 [터미널>재연결]", "Device removed — press Alt+N or use [Terminal > Reconnect]"),
        ["Conn.WaitingReconnect"] = ("장치 분리됨 — 자동 재연결 대기 중… ({0})", "Device removed — waiting to auto-reconnect… ({0})"),
        ["Conn.AutoReconnectOff"] = ("자동 재연결 꺼짐 — Alt+N 또는 [터미널>재연결]", "Auto-reconnect is off — press Alt+N or use [Terminal > Reconnect]"),
        ["Conn.Reconnected"] = ("자동 재연결됨: {0}", "Auto-reconnected: {0}"),
        ["Conn.RetryInUse"] = ("재연결 대기 중… ({0} 사용 중)", "Waiting to reconnect… ({0} in use)"),
        ["Conn.RetryNotReady"] = ("재연결 대기 중… ({0} 준비 중)", "Waiting to reconnect… ({0} not ready)"),
        ["Conn.McpReleased"] = ("AI가 포트 양보 — 외부 작업 대기 중… ({0})", "AI released the port — waiting for the external task… ({0})"),
        ["Conn.McpReopened"] = ("AI가 포트 재연결(uart_open): {0}", "AI reopened the port (uart_open): {0}"),
        ["Conn.McpStillInUse"] = ("재연결 대기 — {0} 아직 사용 중(외부 작업 진행 중?)", "Waiting to reconnect — {0} is still in use (external task running?)"),
        ["Conn.ReopenFailed"] = ("재연결 실패: {0}", "Reconnect failed: {0}"),
        ["Conn.ReconnectCanceledWaiting"] = ("재연결 취소됨 — 자동 재연결 대기 계속 ({0})", "Reconnect canceled — still waiting to auto-reconnect ({0})"),
        ["Conn.ReconnectCanceled"] = ("재연결 취소됨", "Reconnect canceled"),
        ["Conn.PortChangedMcp"] = ("포트 변경 — MCP 재등록 필요: [MCP] 메뉴 > 등록 명령 복사 ({0})", "Port changed — re-register MCP: [MCP] menu > Copy registration command ({0})"),
        ["Conn.InUseBody"] = ("{0} 을(를) 열 수 없습니다.\n다른 프로그램(또는 다른 창/탭)이 사용 중일 수 있습니다.", "Cannot open {0}.\nAnother program (or another window/tab) may be using it."),
        ["Conn.OpenFailedBody"] = ("{0} 연결 실패:\n{1}", "Failed to connect {0}:\n{1}"),
        ["Doc.NotConnectedInput"] = ("연결되지 않음 — 입력 전송 불가", "Not connected — cannot send input"),
        ["Doc.NotConnectedCommand"] = ("연결되지 않음 — 명령 전송 불가", "Not connected — cannot send command"),
        ["Doc.NothingToSave"] = ("저장할 내용이 없습니다 — 입력창에 명령을 입력하세요", "Nothing to save — type a command in the input box"),
        ["Doc.CommandSaveFailed"] = ("명령 저장 실패", "Failed to save the command"),
        ["Doc.CommandSaved"] = ("명령 저장됨: {0}", "Command saved: {0}"),
        ["Doc.CommandSavedInGroup"] = ("명령 저장됨: {0} (그룹: {1})", "Command saved: {0} (group: {1})"),
        ["Doc.NoConnectionToSave"] = ("저장할 연결이 없습니다", "No connection to save"),
        ["Doc.SessionSaveFailed"] = ("세션 저장 실패", "Failed to save the session"),
        ["Doc.SessionSaved"] = ("세션 저장됨: {0} — {1} · {2}{3}", "Session saved: {0} — {1} · {2}{3}"),
        ["Doc.SessionResetSuffix"] = (" · 열 때 리셋", " · reset on open"),
        ["Doc.SessionMcpSuffix"] = (" · 열 때 MCP", " · MCP on open"),
        ["Status.OpenFolderTip"] = ("클릭하면 탐색기에서 이 파일이 있는 폴더를 엽니다", "Click to open this file's folder in Explorer"),
        ["Tip.ToggleConnection"] = ("클릭: 연결 해제 / 재연결", "Click: disconnect / reconnect"),
        ["Menu.ParsePanel"] = ("메시지 파싱 패널(_P)", "Message _Parse Panel"),
        ["Tip.ParsePanel"] = ("메시지 파싱 패널 — 장비→서버 보고를 필드별로 해석", "Message parse panel — decode device-to-server reports field by field"),
        ["Parse.Title"] = ("메시지 파싱", "Message Parse"),
        ["Parse.FollowLatest"] = ("▶ 최신 따라가기", "▶ Follow latest"),
        ["Parse.PickFile"] = ("파일…", "File…"),
        ["Parse.SelectAll"] = ("전체", "All"),
        ["Parse.SelectNone"] = ("해제", "None"),
        ["Parse.ClearTip"] = ("누적된 파싱 데이터와 수신 횟수를 모두 지웁니다", "Clear all accumulated parse data and receive counts"),
        ["Parse.PickFileTip"] = ("파서 정의 파일 선택 (프로젝트마다 다른 정의를 골라 쓴다)", "Pick a parser definition file (different projects, different definitions)"),
        ["Parse.Waiting"] = ("정의된 메시지 수신 대기 중…\n\n터미널에서 텍스트를 선택하면 그 안의 메시지를 고정해 보여줍니다.",
                             "Waiting for a defined message…\n\nSelect text in the terminal to pin the message inside it."),
        ["Parse.NoSpecs"] = ("파서 정의 파일이 없습니다:\n{0}\n\n이 파일에 메시지 키·필드 정의를 넣으면 패널이 해석을 시작합니다. 형식은 README 참조.",
                             "No parser definition file:\n{0}\n\nAdd message keys and field definitions to this file and the panel starts decoding. See README for the format."),
        ["Parse.MissingFields"] = ("(정의보다 값이 {0}개 적음 — 펌웨어/정의 버전 차이)", "({0} fewer values than defined — firmware/definition version mismatch)"),
        ["Parse.Err.Corrupt"] = ("parsers.json 을 읽을 수 없습니다: {0}", "Cannot read parsers.json: {0}"),
        ["Parse.Err.NewerSchema"] = ("parsers.json 의 schemaVersion({0})이 이 버전보다 높습니다.", "parsers.json schemaVersion ({0}) is newer than this app supports."),
        ["Doc.DisconnectWhileLogging"] = ("{0} 은(는) 지금 로깅 중입니다. 연결을 해제하면 그 시점부터의 수신은 파일에 남지 않습니다.\n\n해제할까요?",
                                          "{0} is being logged right now. Disconnecting means nothing after this point is written to the file.\n\nDisconnect?"),
        ["Doc.DisconnectedByUser"] = ("연결 해제: {0} — 점을 다시 누르면 재연결", "Disconnected: {0} — click the dot again to reconnect"),
        ["Doc.McpOnBySession"] = ("세션 설정에 따라 MCP 서버를 켰습니다 — {0}", "MCP server started per the session setting — {0}"),
        ["Doc.SessionNewlineSuffix"] = (" · 개행 ↓{0} ↑{1}", " · new-line ↓{0} ↑{1}"),
        ["Doc.SessionPrompt"] = ("이 연결({0} · {1}bps{2})을 어떤 이름으로 저장할까요?", "Under what name should this connection ({0} · {1} bps{2}) be saved?"),
        ["Doc.SessionPromptTitle"] = ("세션 저장", "Save Session"),
        ["Doc.NothingToSaveLog"] = ("저장할 내용이 없습니다", "Nothing to save"),
        ["Doc.LogSaved"] = ("로그 저장됨: {0}", "Log saved: {0}"),
        ["Doc.LogSaveFailed"] = ("로그 저장 실패: {0}", "Failed to save the log: {0}"),
        ["Doc.LogSaveTitle"] = ("로그 저장", "Save Log"),
        ["Doc.LogFilter"] = ("텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*", "Text file (*.txt)|*.txt|All files (*.*)|*.*"),
        ["Doc.McpCopied"] = ("MCP 등록 명령을 클립보드에 복사했습니다", "MCP registration command copied to the clipboard"),
        ["Doc.FlashRelease"] = ("플래시를 위해 포트 양보 — {0}", "Released the port for flashing — {0}"),
        ["Doc.FlashReopenWait"] = ("재연결 대기 — {0} 아직 사용 중", "Waiting to reconnect — {0} is still in use"),
        ["Doc.PasteConfirm"] = ("여러 줄을 붙여넣습니다. 전송할까요?", "This will paste multiple lines. Send them?"),
        // 위험 명령(restart/erase 등) 승인문. 이것만 한국어로 남아 있으면 영어 사용자가
        // 무엇을 승인하는지 못 읽고 보내게 된다 — 이 묶음에서 유일한 안전 항목이다.
        ["Doc.CommandConfirm"] = ("이 명령을 전송할까요?\n\n{0}", "Send this command?\n\n{0}"),
        ["Doc.CommandGroupMissing"] = ("세션의 명령 그룹 '{0}' 이(가) 없습니다 — '{1}' 을 씁니다.",
                                       "The session's command group '{0}' no longer exists — using '{1}'."),
        // ── 연속 로깅 ──
        ["Doc.LogNeedsConnection"] = ("로깅을 시작하려면 먼저 포트에 연결하세요.",
                                      "Connect to a port before starting logging."),
        ["Doc.LogPickFilter"] = ("로그 파일 (*.log)|*.log|텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
                                 "Log file (*.log)|*.log|Text file (*.txt)|*.txt|All files (*.*)|*.*"),
        ["Log.Title"] = ("로깅", "Logging"),
        ["Log.Filename"] = ("파일", "Filename"),
        ["Log.WriteMode"] = ("쓰기 모드", "Write mode"),
        ["Log.WriteNew"] = ("새로 쓰기 (덮어쓰기)", "New / Overwrite"),
        ["Log.WriteAppend"] = ("이어 쓰기", "Append"),
        ["Log.Format"] = ("기록 형식", "Format"),
        ["Log.FormatScreen"] = ("화면 그대로", "As on screen"),
        ["Log.FormatScreenTip"] = ("화면에 보이는 텍스트를 줄 단위로 기록합니다. 색·커서 이동·CR 덮어쓰기가 반영된 결과라 사람이 읽기 좋습니다(줄이 끝날 때 기록).",
                                   "Writes the text as it appears on screen, line by line — colors, cursor moves and CR overwrites already applied (each line is written when it completes)."),
        ["Log.FormatRaw"] = ("원시 바이트", "Raw bytes"),
        ["Log.FormatRawTip"] = ("수신 바이트를 그대로 기록합니다(ANSI 이스케이프·NUL 포함). 재현·프로토콜 분석용이며 사람이 읽기에는 지저분합니다.",
                                "Writes received bytes verbatim (ANSI escapes and NULs included). For reproduction and protocol analysis; hard to read."),
        ["Log.Timestamp"] = ("줄마다 타임스탬프 기록 [HH:mm:ss.fff]", "Timestamp each line [HH:mm:ss.fff]"),
        ["Log.IncludeBuffer"] = ("화면 버퍼 포함 (시작 시 현재 스크롤백을 먼저 기록)",
                                 "Include screen buffer (write current scrollback first)"),
        ["Log.OverwriteConfirm"] = ("파일이 이미 있습니다. 덮어쓸까요?\n\n{0}",
                                    "The file already exists. Overwrite it?\n\n{0}"),
        ["Log.BadPath"] = ("폴더를 만들 수 없습니다: {0}", "Cannot create the folder: {0}"),
        ["Doc.LogStarted"] = ("로깅 시작: {0}{1}", "Logging started: {0}{1}"),
        ["Doc.LogWithStamps"] = (" (타임스탬프 포함)", " (with timestamps)"),
        ["Doc.LogStopped"] = ("로깅 정지: {0} ({1:N1} KB)", "Logging stopped: {0} ({1:N1} KB)"),
        ["Doc.LogStartFailed"] = ("로깅을 시작하지 못했습니다: {0}", "Could not start logging: {0}"),
        ["Doc.LogFailed"] = ("{0} — 로깅을 정지했습니다.", "{0} — logging has been stopped."),
        ["Log.Err.WriteFailed"] = ("로그 파일 쓰기 실패: {0}", "Log file write failed: {0}"),
        ["Doc.ResetOnOpenOn"] = ("열 때 보드 리셋: 켜짐 — 다음 연결부터 적용(지금 리셋은 Alt+R)", "Reset board on open: on — applies from the next connection (press Alt+R to reset now)"),
        ["Doc.ResetOnOpenOff"] = ("열 때 보드 리셋: 꺼짐", "Reset board on open: off"),
        ["Board.HardReset"] = ("하드웨어 리셋", "Hardware reset"),
        ["Board.Bootloader"] = ("부트로더 진입", "Enter bootloader"),
        ["Board.NotConnected"] = ("{0} 불가 — 연결되지 않음", "{0} not possible — not connected"),
        ["Board.Running"] = ("{0}…", "{0}…"),
        ["Board.Done"] = ("{0} 완료 ({1})", "{0} done ({1})"),
        ["Board.Failed"] = ("{0} 실패 — 연결 상태 확인", "{0} failed — check the connection"),
        ["Board.Error"] = ("{0} 실패: {1}", "{0} failed: {1}"),

        // ── 남은 확인창 · 상태 ──
        ["Sess.ConfirmDelete"] = ("세션을 삭제할까요?\n\n{0}", "Delete this session?\n\n{0}"),
        ["Sess.SaveFailed"] = ("세션을 저장하지 못했습니다.", "Could not save the sessions."),
        ["Sess.DeleteFailed"] = ("세션을 삭제하지 못했습니다.", "Could not delete the session."),
        ["Port.PickOne"] = ("포트를 선택하세요.", "Select a port."),
        ["Cmd.GroupLimit"] = ("그룹은 최대 {0}개입니다.", "At most {0} groups are allowed."),
        ["Cmd.AddGroupTitle"] = ("그룹 추가", "Add Group"),
        ["Cmd.AddGroupPrompt"] = ("새 그룹(프로젝트) 이름:", "Name of the new group (project):"),
        ["Cmd.RenameGroupTitle"] = ("그룹 이름 변경", "Rename Group"),
        ["Cmd.RenameGroupPrompt"] = ("그룹 이름:", "Group name:"),
        ["Cmd.LastGroup"] = ("그룹이 하나뿐입니다 — 삭제할 수 없습니다.", "This is the only group — it cannot be deleted."),
        ["Cmd.ConfirmDeleteGroup"] = ("그룹 '{0}' 과(와) 그 안의 명령을 모두 삭제할까요?", "Delete group '{0}' and all commands in it?"),
        ["Cmd.SubLimit"] = ("폴더 하위 명령은 최대 {0}개입니다.", "At most {0} sub-commands per folder."),
        ["Cmd.ConfirmDeleteFolder"] = ("폴더와 하위 명령 {0}개를 함께 삭제할까요?", "Delete the folder together with its {0} sub-command(s)?"),
        ["Cmd.SaveFailed"] = ("명령을 저장하지 못했습니다.", "Could not save the commands."),
        ["Shell.OnlyOneTab"] = ("분리할 탭이 하나뿐입니다", "Only one tab — nothing to detach"),
        ["Shell.AlreadyMain"] = ("이미 메인 창입니다", "This is already the main window"),
        ["Sess.NewlineDefault"] = ("(기본)", "(default)"),
        ["Sess.NewlineDefaultShort"] = ("기본", "default"),
        ["Sess.NoGroup"] = ("(없음)", "(none)"),
        ["Sess.NoNewline"] = ("(기본 — 현재 설정)", "(default — keep current)"),
        ["Nl.RxCrLf"] = ("CR+LF  (개행=LF)", "CR+LF  (newline=LF)"),
        ["Nl.RxLf"] = ("LF  (CR 무시)", "LF  (ignore CR)"),
        ["Nl.RxCr"] = ("CR  (LF 무시)", "CR  (ignore LF)"),
        ["Nl.RxAuto"] = ("AUTO  (CR·LF 모두)", "AUTO  (CR and LF)"),

        // ── Core 스토어 오류(키 + 인자) ──
        ["Sess.Err.CannotOpen"] = ("세션 파일을 열 수 없습니다({0}). 이번 실행에서는 저장하지 않습니다: {1}", "Cannot open the sessions file ({0}). Nothing will be saved this run: {1}"),
        ["Sess.Err.NewerSchema"] = ("세션 파일이 최신 버전(v{0})에서 만들어졌습니다. 읽기 전용으로 엽니다.", "The sessions file was written by a newer version (v{0}). Opening read-only."),
        ["Sess.Err.Corrupt"] = ("세션 파일이 손상되었습니다: {0}", "The sessions file is corrupt: {0}"),
        ["Sess.Err.CorruptPreserved"] = ("세션 파일이 손상되었습니다: {0} 원본은 {1} 로 보관했습니다.", "The sessions file is corrupt: {0} The original was kept as {1}."),
        ["Sess.Err.Unreadable"] = ("세션 파일을 해석할 수 없습니다({0}). 이번 실행에서는 저장하지 않습니다: {1}", "Cannot parse the sessions file ({0}). Nothing will be saved this run: {1}"),
        ["Sess.Err.ReadOnly"] = ("세션 파일이 읽기 전용 상태여서 저장하지 않았습니다.", "The sessions file is read-only, so nothing was saved."),
        ["Sess.Err.TooMany"] = ("저장된 세션이 최대 {0}개입니다 — 목록에서 삭제 후 다시 저장하세요.", "At most {0} sessions can be stored — delete one and save again."),
        ["Sess.Err.SaveFailed"] = ("세션 파일 저장 실패: {0}", "Could not save the sessions file: {0}"),
        ["Cmd.Err.CannotOpen"] = ("명령 파일을 열 수 없습니다({0}). 이번 실행에서는 저장하지 않습니다: {1}", "Cannot open the commands file ({0}). Nothing will be saved this run: {1}"),
        ["Cmd.Err.NewerSchema"] = ("명령 파일이 최신 버전(v{0})에서 만들어졌습니다. 읽기 전용으로 엽니다.", "The commands file was written by a newer version (v{0}). Opening read-only."),
        ["Cmd.Err.Corrupt"] = ("명령 파일이 손상되었습니다: {0}", "The commands file is corrupt: {0}"),
        ["Cmd.Err.CorruptPreserved"] = ("명령 파일이 손상되었습니다: {0} 원본은 {1} 로 보관했습니다.", "The commands file is corrupt: {0} The original was kept as {1}."),
        ["Cmd.Err.Unreadable"] = ("명령 파일을 해석할 수 없습니다({0}). 이번 실행에서는 저장하지 않습니다: {1}", "Cannot parse the commands file ({0}). Nothing will be saved this run: {1}"),
        ["Cmd.Err.ReadOnly"] = ("명령 파일이 읽기 전용 상태여서 저장하지 않았습니다.", "The commands file is read-only, so nothing was saved."),
        ["Cmd.Err.GroupFull"] = ("그룹 '{0}' 의 명령이 최대 {1}개입니다 — [명령 > 저장 명령 편집]에서 정리하세요.", "Group '{0}' already has the maximum of {1} commands — tidy it up in [Command > Edit Saved Commands]."),
        ["Cmd.Err.TooManyGroups"] = ("명령 그룹이 최대 {0}개입니다.", "At most {0} command groups are allowed."),
        ["Cmd.Err.SaveFailed"] = ("명령 파일 저장 실패: {0}", "Could not save the commands file: {0}"),
        ["Flash.Err.CannotStart"] = ("프로세스를 시작할 수 없습니다.", "Could not start the process."),
        ["Flash.Err.Canceled"] = ("사용자가 취소했습니다.", "Canceled by the user."),
        ["Flash.Err.ExitCode"] = ("esptool 종료 코드 {0}", "esptool exit code {0}"),
        ["Flash.ChipSource.Unknown"] = ("판별 불가", "could not determine"),
        ["Flash.ChipSource.Header"] = ("{0} 헤더(chip_id)", "{0} header (chip_id)"),
        ["Flash.ChipSource.UserPicked"] = ("사용자 지정", "chosen by user"),
        ["Flash.ChipSource.ArgsFile"] = ("flasher_args.json", "flasher_args.json"),
        ["Flash.Err.StartFailed"] = ("esptool 을 시작하지 못했습니다: {0}", "Could not start esptool: {0}"),
        // ── 코드가 만드는 화면 문자열 ──
        ["Doc.TitleNew"] = ("(새 연결)", "(new connection)"),
        ["Doc.TitleDisconnected"] = ("{0} [끊김]", "{0} [disconnected]"),
        ["Doc.TitleReleased"] = ("{0} [AI 양보]", "{0} [yielded to AI]"),
        ["Doc.TitleReconnecting"] = ("{0} [재연결 중…]", "{0} [reconnecting…]"),
        ["Doc.NoCommands"] = ("저장된 명령이 없습니다 — 입력창에 명령을 쓰고 [+ 저장]", "No saved commands — type one below and press [+ Save]"),
        ["Doc.FolderTip"] = ("{0} — 하위 명령 {1}개 (클릭해서 선택)", "{0} — {1} sub-command(s) (click to pick)"),
        ["Doc.ChipTipConfirm"] = ("{0}\n(전송 전 확인 · Ctrl+클릭: 입력창에 채우기)",
                                  "{0}\n(confirms before sending · Ctrl+click: fill the input box)"),
        ["Doc.ChipTip"] = ("{0}\n(Ctrl+클릭: 입력창에 채우기)",
                           "{0}\n(Ctrl+click: fill the input box)"),
        ["Doc.FindNoMatch"] = ("없음", "none"),
        ["Doc.MetricsResetOnOpen"] = ("열 때 리셋", "reset on open"),
        ["Doc.MetricsNotConnected"] = ("(연결 안 됨)", "(not connected)"),
        ["Doc.AiHardReset"] = ("[AI→] 하드웨어 리셋(EN 펄스)", "[AI→] hardware reset (EN pulse)"),
        ["Doc.AiBootloader"] = ("[AI→] 부트로더 진입(IO0=LOW)", "[AI→] enter bootloader (IO0=LOW)"),
        ["About.Version"] = ("버전 {0}", "Version {0}"),
        ["App.CrashBody"] = ("예기치 못한 오류가 발생했지만 계속 실행합니다.\n\n{0}: {1}\n\n자세한 내용: {2}", "An unexpected error occurred, but the app will keep running.\n\n{0}: {1}\n\nDetails: {2}"),
        ["Cmd.NewFolder"] = ("(새 폴더)", "(new folder)"),
        ["Cmd.NewCommand"] = ("(새 명령)", "(new command)"),
        ["Cmd.Header"] = ("명령", "Commands"),
        ["Cmd.HeaderOf"] = ("명령 — {0}", "Commands — {0}"),
        ["Cmd.ConfirmDropEmpty"] = ("전송 문자열이 비어 있는 명령 또는 하위가 없는 폴더 {0}개는 저장되지 않습니다. 계속할까요?", "{0} item(s) with an empty command text or an empty folder will not be saved. Continue?"),
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
