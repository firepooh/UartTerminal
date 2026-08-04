using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UartTerminal.Core.Config;

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

        public string Name { get => _name; set { _name = value ?? ""; Raise(); } }
        public string Port { get; init; } = "";
        public int Baud { get => _baud; set { _baud = value; Raise(); } }
        public string? Group { get => _group; set { _group = value; Raise(); } }

        /// <summary>그룹이 없으면 "(없음)" — 빈칸으로 두면 설정 누락인지 알 수 없다.</summary>
        public string GroupDisplay => string.IsNullOrEmpty(_group) ? "(없음)" : _group!;

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
        }
    }

    /// <summary>그룹 콤보의 "지정 안 함" 항목(null 을 콤보에 담을 수 없어 센티넬 문자열을 쓴다).</summary>
    private const string NoGroup = "(없음)";

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
            _rows.Add(new Row { Name = s.Name, Port = s.Port, Baud = s.Baud, Group = s.CommandGroup });

        SessionList.ItemsSource = _rows;

        BaudBox.ItemsSource = BaudPresets;
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
            MessageBox.Show(owner, err, "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);

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

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not Row row) return;
        var r = MessageBox.Show(this, $"세션을 삭제할까요?\n\n{row.Name} — {row.Port} · {row.Baud}",
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
            CommandGroup = r.Group,
        });

        if (!_sessions.ReplaceAll(items))
        {
            // 실패 시 창을 닫지 않는다(편집 내용을 잃지 않게).
            MessageBox.Show(this, _sessions.LastError ?? "세션을 저장하지 못했습니다.",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }
}
