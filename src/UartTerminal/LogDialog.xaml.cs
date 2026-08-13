using System.IO;
using System.Windows;

namespace UartTerminal;

/// <summary>연속 로깅 시작 옵션(다이얼로그 결과).</summary>
public sealed record LogOptions
{
    public required string Path { get; init; }
    /// <summary>true = 기존 파일 끝에 이어 쓰기(TeraTerm 의 Append).</summary>
    public required bool Append { get; init; }
    /// <summary>줄마다 <c>[HH:mm:ss.fff]</c> 수신 시각을 붙일지.</summary>
    public required bool Timestamps { get; init; }
    /// <summary>시작 시 현재 화면 버퍼(스크롤백)를 먼저 기록할지.</summary>
    public required bool IncludeScreenBuffer { get; init; }

    /// <summary>화면 그대로(기본) / 수신 바이트 그대로.</summary>
    public required UartTerminal.Core.Logging.LogFormat Format { get; init; }
}

/// <summary>
/// 연속 로깅 시작 다이얼로그(TeraTerm 의 Log 다이얼로그에 해당 — 필요한 것만: 파일·쓰기 모드·
/// 타임스탬프·화면 버퍼 포함). 옵션들이 메뉴에 흩어져 있던 것을 여기로 모았다 —
/// 로깅을 시작하는 순간에만 의미 있는 선택들이라 시작 지점에서 한 번에 고르는 게 맞다.
/// 선택값은 전역 기본값(state.json)으로 저장돼 다음 시작 때 그대로 온다.
/// </summary>
public partial class LogDialog : Window
{
    private readonly AppState _state;

    private LogDialog(AppState state, string defaultFileName, string? sessionFolder)
    {
        InitializeComponent();
        _state = state;

        PathBox.Text = Prefill(state, defaultFileName, sessionFolder);

        ModeAppend.IsChecked = state.LogAppend;
        ModeNew.IsChecked = !state.LogAppend;
        FormatRaw.IsChecked = state.LogRaw;
        FormatScreen.IsChecked = !state.LogRaw;
        TimestampCheck.IsChecked = state.LogTimestamps;
        IncludeBufferCheck.IsChecked = state.LogIncludeBuffer;
    }

    /// <summary>
    /// 미리 채울 경로. <b>이어 쓰기</b>는 "그 파일에 계속 쌓는다" 는 뜻이므로 마지막 파일을 그대로 두고,
    /// <b>새로 쓰기</b>는 폴더만 기억하고 이름은 규칙으로 새로 만든다.
    ///
    /// 파일 전체를 기억하면 포트를 두 개 열었을 때 두 번째 탭이 <b>첫 탭과 같은 파일</b>을 제안받는다 —
    /// 그대로 확인을 누르면 새로 쓰기는 앞 로그를 날리고 이어 쓰기는 두 보드 출력이 한 파일에 섞인다.
    /// 폴더 우선순위는 <b>세션 → 마지막에 쓴 폴더 → 내 문서</b>(세션이 가장 구체적인 의도).
    /// </summary>
    private static string Prefill(AppState state, string defaultFileName, string? sessionFolder)
    {
        string? last = state.LastLogFile;
        if (state.LogAppend && !string.IsNullOrEmpty(last)) return last!;

        string folder = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionFolder)) folder = sessionFolder!.Trim();
            else if (!string.IsNullOrEmpty(last) && Path.GetDirectoryName(last) is { Length: > 0 } d) folder = d;
        }
        catch { folder = ""; }   // 손편집된 경로가 들어와도 다이얼로그는 떠야 한다

        if (folder.Length == 0)
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        try { return Path.Combine(folder, defaultFileName); }
        catch { return defaultFileName; }
    }

    /// <summary>옵션을 받아 돌려준다(취소면 null). 확정된 선택은 다음 시작의 기본값으로 저장된다.</summary>
    /// <param name="sessionFolder">세션이 지정한 로그 폴더(없으면 null).</param>
    public static LogOptions? Ask(Window? owner, AppState state, string defaultFileName, string? sessionFolder)
    {
        var dlg = new LogDialog(state, defaultFileName, sessionFolder);
        if (owner is not null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;

        return new LogOptions
        {
            Path = dlg.PathBox.Text.Trim(),
            Append = dlg.ModeAppend.IsChecked == true,
            Timestamps = dlg.TimestampCheck.IsChecked == true,
            IncludeScreenBuffer = dlg.IncludeBufferCheck.IsChecked == true,
            Format = dlg.FormatRaw.IsChecked == true
                ? UartTerminal.Core.Logging.LogFormat.Raw
                : UartTerminal.Core.Logging.LogFormat.Screen,
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.S("Log.Title"),
            Filter = Loc.S("Doc.LogPickFilter"),
            // 덮어쓸지는 쓰기 모드가 정한다(이어 쓰기면 기존 파일 선택이 정상) → OK 에서 확인
            OverwritePrompt = false,
        };
        try
        {
            string cur = PathBox.Text.Trim();
            if (cur.Length > 0)
            {
                dlg.FileName = Path.GetFileName(cur);
                if (Path.GetDirectoryName(cur) is { Length: > 0 } dir && Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }
        }
        catch { /* 경로가 이상해도 찾아보기는 열려야 한다 */ }

        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string path = PathBox.Text.Trim();
        if (path.Length == 0) return;

        // 폴더가 없으면 만들어 본다 — 손으로 친 경로의 흔한 상태
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.F("Log.BadPath", ex.Message), "UartTerminal",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 새로 쓰기 모드에서 기존 파일은 확인을 받는다(찾아보기의 자동 확인을 껐으므로 여기서).
        if (ModeNew.IsChecked == true && File.Exists(path))
        {
            var r = MessageBox.Show(this, Loc.F("Log.OverwriteConfirm", path), "UartTerminal",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (r != MessageBoxResult.OK) return;
        }

        // 다음 시작의 기본값으로 저장
        _state.LastLogFile = path;
        _state.LogAppend = ModeAppend.IsChecked == true;
        _state.LogRaw = FormatRaw.IsChecked == true;
        _state.LogTimestamps = TimestampCheck.IsChecked == true;
        _state.LogIncludeBuffer = IncludeBufferCheck.IsChecked == true;
        _state.Save();

        DialogResult = true;
    }
}
