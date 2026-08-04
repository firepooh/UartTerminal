using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UartTerminal.Core.Config;
using UartTerminal.Core.Terminal;

namespace UartTerminal;

/// <summary>
/// 세션(접속 프로필) 관리 — 이름·포트·속도·연결된 명령 그룹을 표로 한눈에 보고 편집한다.
/// 접속 다이얼로그(PortSelectDialog)는 '고르는' 화면이라 정보가 축약돼 있어(그룹 미표시),
/// 설정을 확인·정리하는 전용 화면을 따로 둔다. 포트는 여기서 바꾸지 않는다(연결 시 폼에서 결정 — 진실의 출처 하나).
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
        private bool _resetOnOpen;
        private ReceiveNewline? _nlRx;
        private TransmitNewline? _nlTx;

        public string Name { get => _name; set { _name = value ?? ""; Raise(); } }
        public string Port { get; init; } = "";
        public int Baud { get => _baud; set { _baud = value; Raise(); } }
        public string? Group { get => _group; set { _group = value; Raise(); } }
        public bool ResetOnOpen { get => _resetOnOpen; set { _resetOnOpen = value; Raise(); } }

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

        /// <summary>켜짐만 눈에 띄게 — 꺼짐은 흐린 "—" 로 둬서 표가 시끄러워지지 않게.</summary>
        public string ResetDisplay => _resetOnOpen ? Loc.S("Sess.Reset") : "—";

        public Brush ResetBrush => _resetOnOpen
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResetDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResetBrush)));
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

    private static readonly int[] BaudPresets = { 74880, 115200, 230400, 460800, 921600 };

    private static bool _open;

    private readonly SessionStore _sessions;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _syncing; // 편집 컨트롤 → 모델 역방향 갱신 재진입 방지

    private SessionManagerDialog(SessionStore sessions, CommandStore commands)
    {
        InitializeComponent();
        _sessions = sessions;
        PathText.Text = sessions.FilePath;

        foreach (var s in sessions.Items)
            _rows.Add(new Row
            {
                Name = s.Name, Port = s.Port, Baud = s.Baud,
                ResetOnOpen = s.ResetOnOpen, Group = s.CommandGroup,
                NewlineRx = s.NewlineRx, NewlineTx = s.NewlineTx,
            });

        SessionList.ItemsSource = _rows;

        BaudBox.ItemsSource = BaudPresets;
        RxBox.ItemsSource = RxItems.Select(i => i.Text).ToList();
        TxBox.ItemsSource = TxItems.Select(i => i.Text).ToList();
        // 그룹 목록 = commands.json 의 그룹 + "(없음)". 연결이 끊긴 그룹명(파일에서 지워진 경우)도 보존해 보여준다.
        var groups = new List<string> { NoGroup };
        groups.AddRange(commands.GroupNames);
        foreach (var r in _rows)
            if (!string.IsNullOrEmpty(r.Group) && !groups.Contains(r.Group!)) groups.Add(r.Group!);
        GroupBox.ItemsSource = groups;

        if (_rows.Count > 0) SessionList.SelectedIndex = 0;
        UpdateEditPane();
    }

    /// <summary>세션 관리자를 모달로 띄운다. 열기 전에 파일을 다시 읽어 외부 변경을 반영.</summary>
    public static void ShowManager(SessionStore sessions, CommandStore commands, Window? owner)
    {
        if (_open) return;

        sessions.Load();
        if (sessions.LastError is { } err)
            MessageBox.Show(owner, Loc.Format(err), "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);

        _open = true;
        try
        {
            var dlg = new SessionManagerDialog(sessions, commands);
            if (owner is not null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
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

        _syncing = true;
        try
        {
            NameBox.Text = row?.Name ?? "";
            BaudBox.SelectedItem = row is null ? null : (object)row.Baud;
            GroupBox.SelectedItem = row is null ? null : (string.IsNullOrEmpty(row.Group) ? NoGroup : row.Group!);
            ResetCheck.IsChecked = row?.ResetOnOpen ?? false;
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

    private void BaudBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not Row row) return;
        if (BaudBox.SelectedItem is int b) row.Baud = b;
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
        var items = _rows.Select(r => new SessionProfile
        {
            Name = r.Name,
            Port = r.Port,
            Baud = r.Baud,
            ResetOnOpen = r.ResetOnOpen,
            NewlineRx = r.NewlineRx,
            NewlineTx = r.NewlineTx,
            CommandGroup = r.Group,
        });

        if (!_sessions.ReplaceAll(items))
        {
            // 실패 시 창을 닫지 않는다(편집 내용을 잃지 않게).
            MessageBox.Show(this, Loc.FormatOrNull(_sessions.LastError) ?? Loc.S("Sess.SaveFailed"),
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }
}
