using UartTerminal.Core.Serial;
using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UartTerminal.Core.Config;
using UartTerminal.Core.Terminal;

namespace UartTerminal;

/// <summary>
/// 세션(접속 프로필) 관리 — 이름·포트·속도·연결된 명령 그룹을 표로 한눈에 보고 편집한다.
/// 접속 다이얼로그(PortSelectDialog)는 '고르는' 화면이라 정보가 축약돼 있어(그룹 미표시),
/// 설정을 확인·정리하는 전용 화면을 따로 둔다. 세션을 더블클릭하면 <b>지금 탭에 적용</b>한다.
/// <b>모달</b>이며 열 때 파일을 다시 읽어 손편집/다른 인스턴스 변경을 반영한다.
/// </summary>
public partial class SessionManagerDialog : Window
{
    /// <summary>편집 중인 한 세션.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private string _name = "";
        private int _baud = 115200;
        private string? _group;
        private string _port = "";
        private bool _resetOnOpen;
        private bool _mcpOnOpen;
        private string? _logFolder;
        private ReceiveNewline? _nlRx;
        private TransmitNewline? _nlTx;

        public string Name { get => _name; set { _name = value ?? ""; Raise(); } }
        public string Port { get => _port; set { _port = (value ?? "").Trim(); Raise(); } }
        public int Baud { get => _baud; set { _baud = value; Raise(); } }
        public string? Group { get => _group; set { _group = value; Raise(); } }
        public bool ResetOnOpen { get => _resetOnOpen; set { _resetOnOpen = value; Raise(); } }
        public bool McpOnOpen { get => _mcpOnOpen; set { _mcpOnOpen = value; Raise(); } }

        /// <summary>로그 저장 폴더(파일명이 아니다). 비면 마지막에 쓴 폴더를 따라간다.</summary>
        public string? LogFolder { get => _logFolder; set { _logFolder = value; Raise(); } }

        /// <summary>null = 지정 없음(접속 시 현재 값 유지).</summary>
        public ReceiveNewline? NewlineRx { get => _nlRx; set { _nlRx = value; Raise(); } }
        public TransmitNewline? NewlineTx { get => _nlTx; set { _nlTx = value; Raise(); } }

        /// <summary>"↓CR+LF ↑CR" 형태. 둘 다 지정 없으면 흐린 "(기본)".</summary>
        public string NewlineDisplay => _nlRx is null && _nlTx is null
            ? Loc.S("Sess.NewlineDefault")
            : $"↓{(_nlRx?.Label() ?? Loc.S("Sess.NewlineDefaultShort"))} ↑{(_nlTx?.Label() ?? Loc.S("Sess.NewlineDefaultShort"))}";

        public Brush NewlineBrush => _nlRx is null && _nlTx is null
            ? (Brush)Application.Current.Resources["TextFaint"]
            : (Brush)Application.Current.Resources["TextDim"];

        /// <summary>
        /// 열 때 하는 일을 한 칸에 모은다("리셋 · MCP"). 켜짐만 눈에 띄게 — 아무것도 없으면
        /// 흐린 "—" 로 둬서 표가 시끄러워지지 않게(항목마다 열을 늘리면 이름 열이 먼저 잘린다).
        /// </summary>
        public string OnOpenDisplay
        {
            get
            {
                var on = new List<string>(2);
                if (_resetOnOpen) on.Add(Loc.S("Sess.Reset"));
                if (_mcpOnOpen) on.Add(Loc.S("Sess.Mcp"));
                return on.Count == 0 ? Loc.S("Sess.OnOpenNone") : string.Join(" · ", on);
            }
        }

        public Brush OnOpenBrush => _resetOnOpen || _mcpOnOpen
            ? (Brush)Application.Current.Resources["Amber"]
            : (Brush)Application.Current.Resources["TextFaint"];

        /// <summary>그룹이 없으면 "(없음)" — 빈칸으로 두면 설정 누락인지 알 수 없다.</summary>
        public string GroupDisplay => string.IsNullOrEmpty(_group) ? Loc.S("Sess.NoGroup") : _group!;

        /// <summary>그룹 미지정은 흐리게, 지정은 강조색으로 — 표에서 바로 구분되게.</summary>
        public Brush GroupBrush => string.IsNullOrEmpty(_group)
            ? (Brush)Application.Current.Resources["TextFaint"]
            : (Brush)Application.Current.Resources["Purple"];

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnOpenDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnOpenBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NewlineDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NewlineBrush)));
        }
    }

    /// <summary>그룹 콤보의 "지정 안 함" 항목(null 을 콤보에 담을 수 없어 센티넬 문자열을 쓴다).</summary>
    private static string NoGroup => Loc.S("Sess.NoGroup");

    /// <summary>개행 콤보의 "지정 안 함" — 세션에 값을 두지 않고 접속 시 현재 설정을 따른다.</summary>
    private static string NoNewline => Loc.S("Sess.NoNewline");

    // 콤보 항목: 표시 문자열 ↔ enum 값. (기본)은 null.
    // 라벨은 콤보 폭에 맞춰 짧게 — 자세한 규칙은 ToolTip/README §2.1 에 있다.
    // static readonly 로 두면 시작 시 언어로 고정된다 → 매번 조회하는 프로퍼티.
    private static (string Text, ReceiveNewline? Value)[] RxItems => new (string, ReceiveNewline?)[]
    {
        (NoNewline, null),
        (Loc.S("Nl.RxCrLf"), ReceiveNewline.CrLf),
        (Loc.S("Nl.RxLf"), ReceiveNewline.Lf),
        (Loc.S("Nl.RxCr"), ReceiveNewline.Cr),
        (Loc.S("Nl.RxAuto"), ReceiveNewline.Auto),
    };

    private static (string Text, TransmitNewline? Value)[] TxItems => new (string, TransmitNewline?)[]
    {
        (NoNewline, null),
        ("CR", TransmitNewline.Cr),
        ("CR+LF", TransmitNewline.CrLf),
        ("LF", TransmitNewline.Lf),
    };


    private static bool _open;

    private readonly SessionStore _sessions;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _syncing; // 편집 컨트롤 → 모델 역방향 갱신 재진입 방지

    private SessionManagerDialog(SessionStore sessions, CommandStore commands, string? initialSession)
    {
        InitializeComponent();
        _sessions = sessions;
        PathText.Text = sessions.FilePath;

        foreach (var s in sessions.Items)
            _rows.Add(new Row
            {
                Name = s.Name, Port = s.Port, Baud = s.Baud,
                ResetOnOpen = s.ResetOnOpen, McpOnOpen = s.McpOnOpen, Group = s.CommandGroup,
                NewlineRx = s.NewlineRx, NewlineTx = s.NewlineTx, LogFolder = s.LogFolder,
            });

        SessionList.ItemsSource = _rows;
        SessionListEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        BaudBox.ItemsSource = BaudRates.Common;
        RxBox.ItemsSource = RxItems.Select(i => i.Text).ToList();
        TxBox.ItemsSource = TxItems.Select(i => i.Text).ToList();
        // 그룹 목록 = commands.json 의 그룹 + "(없음)". 연결이 끊긴 그룹명(파일에서 지워진 경우)도 보존해 보여준다.
        var groups = new List<string> { NoGroup };
        groups.AddRange(commands.GroupNames);
        foreach (var r in _rows)
            if (!string.IsNullOrEmpty(r.Group) && !groups.Contains(r.Group!)) groups.Add(r.Group!);
        GroupBox.ItemsSource = groups;

        // 열자마자 현재 탭이 쓰는 세션을 고른다 — 세션으로 연 탭에서 관리자를 열었는데
        // 첫 줄이 잡혀 있으면, 목록이 길 때 내 세션을 다시 찾아 골라야 한다.
        var start = _rows.FirstOrDefault(
            r => string.Equals(r.Name, initialSession, StringComparison.OrdinalIgnoreCase)) ?? _rows.FirstOrDefault();
        SessionList.SelectedItem = start;
        // 목록이 길면 고른 줄이 화면 밖에 있을 수 있다 — 레이아웃이 끝난 뒤에 보이는 곳으로 끌어온다.
        if (start is not null)
            Loaded += (_, _) => { SessionList.ScrollIntoView(start); SessionList.Focus(); };
        UpdateEditPane();
    }

    /// <summary>세션 관리자를 모달로 띄운다. 열기 전에 파일을 다시 읽어 외부 변경을 반영.
    /// <paramref name="initialSession"/> 을 주면 그 세션을 골라둔다(없거나 못 찾으면 첫 세션).</summary>
    /// <summary>
    /// 돌려주는 값 = <b>더블클릭으로 "지금 탭에 적용" 을 요청한 세션</b>(그냥 저장/취소면 null).
    /// 적용은 호출자가 한다 — 이 창은 어떤 탭이 활성인지 모른다.
    /// </summary>
    public static SessionProfile? ShowManager(SessionStore sessions, CommandStore commands, Window? owner, string? initialSession = null)
    {
        if (_open) return null;

        sessions.Load();
        if (sessions.LastError is { } err)
            MessageBox.Show(owner, Loc.Format(err), "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);

        _open = true;
        try
        {
            var dlg = new SessionManagerDialog(sessions, commands, initialSession);
            if (owner is not null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return dlg.Applied;
        }
        finally
        {
            _open = false;
        }
    }

    // ── 선택 / 편집 ──────────────────────────────────────────────────────────────

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEditPane();

    private void UpdateEditPane()
    {
        var row = SessionList.SelectedItem as Row;
        EditPane.IsEnabled = row is not null;
        DeleteButton.IsEnabled = row is not null;
        ApplyButton.IsEnabled = row is not null;

        _syncing = true;
        try
        {
            NameBox.Text = row?.Name ?? "";
            FillPorts(row?.Port);
            SetBaudBox(row?.Baud);
            GroupBox.SelectedItem = row is null ? null : (string.IsNullOrEmpty(row.Group) ? NoGroup : row.Group!);
            ResetCheck.IsChecked = row?.ResetOnOpen ?? false;
            McpCheck.IsChecked = row?.McpOnOpen ?? false;
            LogFolderBox.Text = row?.LogFolder ?? "";
            RxBox.SelectedItem = RxItems.First(i => i.Value == row?.NewlineRx).Text;
            TxBox.SelectedItem = TxItems.First(i => i.Value == row?.NewlineTx).Text;
        }
        finally { _syncing = false; }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        row.Name = NameBox.Text;
    }

    /// <summary>
    /// 지금 연결된 포트에 <b>이 세션에 저장된 값</b>을 얹어 목록을 만든다.
    /// 저장된 포트가 지금 안 꽂혀 있어도 목록에서 사라지면 안 된다 — 그러면 선택이 풀려
    /// 사용자가 건드리지도 않은 세션의 포트가 조용히 바뀐다.
    /// </summary>
    private void FillPorts(string? current)
    {
        var names = new List<string>();
        try { names.AddRange(System.IO.Ports.SerialPort.GetPortNames()); }
        catch (Exception ex) { DiagLog.Warn($"포트 열거 실패: {ex.Message}"); }

        if (!string.IsNullOrWhiteSpace(current) &&
            !names.Contains(current!, StringComparer.OrdinalIgnoreCase))
            names.Add(current!);

        names.Sort(PortOrder);
        PortBox.ItemsSource = names;

        // 편집 가능 ComboBox 는 ItemsSource 를 갈아끼운 직후 Text 만 넣으면 템플릿이 붙을 때 지워진다
        // (선택이 없으면 Text 를 비우는 기본 동작). 목록에 있는 값은 SelectedItem 으로 넣어야 남는다.
        int at = names.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) PortBox.SelectedIndex = at;
        else { PortBox.SelectedIndex = -1; PortBox.Text = current ?? ""; }
    }

    /// <summary>COM2 가 COM10 뒤로 가지 않게 숫자 부분으로 비교한다.</summary>
    private static readonly Comparison<string> PortOrder = (a, b) =>
    {
        int na = Num(a), nb = Num(b);
        if (na >= 0 && nb >= 0 && na != nb) return na.CompareTo(nb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

        static int Num(string s)
        {
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            return i < s.Length && int.TryParse(s[i..], out int n) ? n : -1;
        }
    };

    /// <summary>
    /// 목록에서 고른 경우. <b>Text 가 아니라 SelectedItem 을 본다</b> —
    /// 편집 가능 콤보는 이 시점에 Text 가 아직 옛 값일 수 있다.
    /// </summary>
    private void PortBox_Picked(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox.SelectedItem is string picked) ApplyPort(picked);
    }

    /// <summary>
    /// 직접 친 경우. 반대로 <b>SelectedItem 은 옛 선택으로 남아 있으므로 Text 를 본다.</b>
    /// 포커스를 옮길 때 확정한다(한 글자 칠 때마다 목록이 다시 그려지지 않게).
    /// </summary>
    private void PortBox_Typed(object sender, RoutedEventArgs e) => ApplyPort(PortBox.Text);

    /// <summary>
    /// 빈 값은 되돌린다 — 포트 없는 세션은 접속에 쓸 수 없어 저장할 이유가 없다.
    /// 형식은 검사하지 않는다: 막아서 얻는 것보다 못 적게 되는 쪽이 손해가 크고,
    /// 틀린 포트는 접속할 때 바로 드러난다.
    /// </summary>
    private void ApplyPort(string? value)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;

        string text = (value ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0) { PortBox.Text = row.Port; return; }
        if (string.Equals(text, row.Port, StringComparison.Ordinal)) return;

        row.Port = text;
        PortBox.Text = text;   // 소문자로 쳤을 때 화면도 맞춰 준다
    }

    /// <summary>
    /// 목록에 있으면 골라 주고, 없으면 글자로 넣는다 — 손으로 적어 둔 속도(예: 1000000)가
    /// 세션을 다시 열 때 조용히 사라지면 안 된다.
    /// </summary>
    private void SetBaudBox(int? baud)
    {
        if (baud is not int v) { BaudBox.SelectedIndex = -1; BaudBox.Text = ""; return; }
        int at = Array.IndexOf(BaudRates.Common, v);
        if (at >= 0) BaudBox.SelectedIndex = at;
        else { BaudBox.SelectedIndex = -1; BaudBox.Text = v.ToString(CultureInfo.InvariantCulture); }
    }

    private void BaudBox_Picked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        if (BaudBox.SelectedItem is int b) row.Baud = b;
    }

    /// <summary>직접 친 값. 못 읽으면 되돌린다.</summary>
    private void BaudBox_Typed(object sender, RoutedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        if (BaudRates.TryParse(BaudBox.Text, out int b)) { row.Baud = b; SetBaudBox(b); }
        else SetBaudBox(row.Baud);
    }

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        if (GroupBox.SelectedItem is string g) row.Group = g == NoGroup ? null : g;
    }

    private void ResetCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        row.ResetOnOpen = ResetCheck.IsChecked == true;
    }

    private void McpCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        row.McpOnOpen = McpCheck.IsChecked == true;
    }

    private void LogFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        string t = LogFolderBox.Text.Trim();
        row.LogFolder = t.Length == 0 ? null : t;
    }

    private void LogFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.S("Sess.PickLogFolder") };
        try
        {
            string cur = LogFolderBox.Text.Trim();
            if (cur.Length > 0 && Directory.Exists(cur)) dlg.InitialDirectory = cur;
        }
        catch { /* 손편집된 경로여도 선택창은 열려야 한다 */ }

        if (dlg.ShowDialog(this) == true) LogFolderBox.Text = dlg.FolderName;
    }

    /// <summary>비우면 '지정 없음' — 로깅 시작 때 마지막에 쓴 폴더를 따라간다.</summary>
    private void LogFolderClear_Click(object sender, RoutedEventArgs e) => LogFolderBox.Clear();

    private void RxBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        if (RxBox.SelectedItem is string t) row.NewlineRx = RxItems.First(i => i.Text == t).Value;
    }

    private void TxBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        if (TxBox.SelectedItem is string t) row.NewlineTx = TxItems.First(i => i.Text == t).Value;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not Row row) return;
        var r = MessageBox.Show(this, Loc.F("Sess.ConfirmDelete", $"{row.Name} — {row.Port} · {row.Baud}"),
            "UartTerminal", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return;

        int i = _rows.IndexOf(row);
        _rows.Remove(row);
        if (_rows.Count > 0) SessionList.SelectedIndex = Math.Min(i, _rows.Count - 1);
        UpdateEditPane();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_sessions.FilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagLog.Warn($"세션 파일 폴더 열기 실패: {ex.Message}");
        }
    }

    // ── 저장 ─────────────────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAll()) DialogResult = true;
    }

    /// <summary>
    /// 고른 세션을 <b>지금 탭에 적용</b>하고 닫는다. 먼저 저장한다 —
    /// 방금 고친 값으로 접속하는 것이 기대에 맞고, 편집을 버리고 옛 값으로 붙으면 혼란스럽다.
    /// </summary>
    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 목록 여백을 더블클릭한 경우까지 잡지 않게 — 실제 항목 위에서만 반응한다.
        if (ItemsControl.ContainerFromElement(SessionList, (DependencyObject)e.OriginalSource) is not ListBoxItem)
            return;
        ApplySelected();
    }

    private void ApplyToTab_Click(object sender, RoutedEventArgs e) => ApplySelected();

    private void ApplySelected()
    {
        if (SessionList.SelectedItem is not Row row) return;
        if (!SaveAll()) return;

        Applied = ToProfile(row);
        DialogResult = true;
    }

    /// <summary>더블클릭으로 고른 세션(없으면 null). <see cref="ShowManager"/> 가 호출자에게 넘긴다.</summary>
    private SessionProfile? Applied { get; set; }

    private static SessionProfile ToProfile(Row r) => new()
    {
        Name = r.Name,
        Port = r.Port,
        Baud = r.Baud,
        ResetOnOpen = r.ResetOnOpen,
        McpOnOpen = r.McpOnOpen,
        LogFolder = r.LogFolder,
        NewlineRx = r.NewlineRx,
        NewlineTx = r.NewlineTx,
        CommandGroup = r.Group,
    };

    private bool SaveAll()
    {
        if (_sessions.ReplaceAll(_rows.Select(ToProfile))) return true;

        // 실패 시 창을 닫지 않는다(편집 내용을 잃지 않게).
        MessageBox.Show(this, Loc.FormatOrNull(_sessions.LastError) ?? Loc.S("Sess.SaveFailed"),
            "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }
}
