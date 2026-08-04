using System.Windows;
using System.Windows.Controls;
using UartTerminal.Core.Config;
using UartTerminal.Core.Serial;

namespace UartTerminal;

/// <summary>
/// 연결 다이얼로그. 저장된 세션(프로필) 목록과 감지된 포트 목록을 함께 보여준다.
/// 세션을 고르면 폼(포트·속도)이 채워질 뿐이며 <b>확정 값은 항상 폼에서 읽는다</b> — 진실의 출처를 하나로 유지.
/// 세션을 쓰지 않는 사용자를 위해 "포트만 골라 바로 연결" 경로는 항상 노출한다(README §2 철학).
/// </summary>
public partial class PortSelectDialog : Window
{
    /// <summary>
    /// 선택 가능한 통신 속도(README §2). ESP-IDF 개발에서 실제로 쓰이는 값만 둔다:
    /// 74880=ROM 부트로더 출력(부트루프 진단), 115200=기본 콘솔, 230400/460800/921600=고속 로그.
    /// </summary>
    public static readonly int[] BaudPresets = { 74880, 115200, 230400, 460800, 921600 };

    public const int DefaultBaud = 115200;

    private readonly SessionStore? _sessions;

    /// <summary>'열 때 보드 리셋' 은 전역 설정이라 다이얼로그가 직접 읽고 쓴다(없으면 체크박스 숨김).</summary>
    private readonly AppState? _state;

    /// <summary>세션 선택으로 지정된 포트(감지 목록에 없을 수도 있다 — 그 경우 자동 재연결 대기로 이어진다).</summary>
    private string? _sessionPort;

    /// <summary>세션 선택으로 지정된 명령 그룹(접속 확정 시 SelectedCommandGroup 으로 넘어간다).</summary>
    private string? _sessionCommandGroup;

    public PortInfo? SelectedPort { get; private set; }

    /// <summary>사용자가 고른 통신 속도. 취소 시 의미 없음.</summary>
    public int SelectedBaud { get; private set; } = DefaultBaud;

    /// <summary>세션으로 접속한 경우 그 세션에 연결된 명령 그룹(없으면 null → 그룹 자동 전환 안 함).</summary>
    public string? SelectedCommandGroup { get; private set; }

    public PortSelectDialog(string? preselectPort = null, int preselectBaud = DefaultBaud,
                            SessionStore? sessions = null, AppState? state = null)
    {
        InitializeComponent();
        _sessions = sessions;
        _state = state;
        if (state is null) ResetOnOpenCheck.Visibility = Visibility.Collapsed;
        else ResetOnOpenCheck.IsChecked = state.ResetOnOpen;
        BuildBaudChips(preselectBaud);
        LoadSessions();
        RefreshPorts(preselectPort);
    }

    /// <summary>전역 설정이므로 즉시 저장한다(취소해도 유지 — 메뉴의 [포트 열 때 보드 리셋] 과 같은 값).</summary>
    private void ResetOnOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        _state.ResetOnOpen = ResetOnOpenCheck.IsChecked == true;
        _state.Save();
    }

    // ── 세션 ─────────────────────────────────────────────────────────────────────

    private void LoadSessions()
    {
        var items = _sessions?.Items ?? (IReadOnlyList<SessionProfile>)Array.Empty<SessionProfile>();
        // 저장된 세션이 없으면 섹션 자체를 숨겨, 세션을 쓰지 않는 사용자에겐 기존과 같은 화면이 되게 한다.
        SessionSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionList.ItemsSource = items;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteSessionButton.IsEnabled = SessionList.SelectedItem is SessionProfile;
        if (SessionList.SelectedItem is not SessionProfile s) return;

        // 세션은 '폼을 채우는 바로가기'다. 포트가 감지 목록에 있으면 그 항목을 선택하고,
        // 없으면 세션의 포트명을 기억해 두었다가 연결 시 사용한다(자동 재연결 대기로 이어짐).
        _sessionPort = s.Port;
        _sessionCommandGroup = s.CommandGroup; // 접속 후 칩 바를 이 그룹으로 자동 전환
        SetBaud(s.Baud);

        var match = (PortList.ItemsSource as IEnumerable<PortInfo>)?
            .FirstOrDefault(p => string.Equals(p.PortName, s.Port, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            PortList.SelectionChanged -= PortList_SelectionChanged; // 세션 선택이 지워지지 않게
            PortList.SelectedItem = match;
            PortList.SelectionChanged += PortList_SelectionChanged;
        }
        else
        {
            PortList.SelectionChanged -= PortList_SelectionChanged;
            PortList.SelectedItem = null;
            PortList.SelectionChanged += PortList_SelectionChanged;
        }
    }

    private void SessionList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile) Accept();
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions is null || SessionList.SelectedItem is not SessionProfile s) return;

        var r = MessageBox.Show(this, $"세션을 삭제할까요?\n\n{s.Display}", "UartTerminal",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return;

        if (!_sessions.Remove(s))
        {
            MessageBox.Show(this, _sessions.LastError ?? "세션을 삭제하지 못했습니다.",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _sessionPort = null;
        LoadSessions();
    }

    // ── 포트 / 속도 ──────────────────────────────────────────────────────────────

    private void PortList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 사용자가 포트를 직접 고르면 세션 바로가기는 해제한다(폼이 실제 선택을 반영).
        if (PortList.SelectedItem is not PortInfo) return;
        _sessionPort = null;
        SessionList.SelectedItem = null;
    }

    /// <summary>속도 세그먼트를 프리셋에서 생성(RadioButton 이라 배타 선택은 프레임워크가 처리).</summary>
    private void BuildBaudChips(int preselect)
    {
        int selected = BaudPresets.Contains(preselect) ? preselect : DefaultBaud;
        foreach (int baud in BaudPresets)
        {
            var rb = new RadioButton
            {
                Content = baud.ToString(),
                Tag = baud,
                GroupName = "Baud",
                IsChecked = baud == selected,
                Style = (Style)FindResource("BaudChip"),
            };
            BaudHost.Children.Add(rb);
        }
        SelectedBaud = selected;
    }

    private void SetBaud(int baud)
    {
        foreach (var child in BaudHost.Children)
            if (child is RadioButton rb && rb.Tag is int b)
                rb.IsChecked = b == baud;
    }

    private int CheckedBaud()
    {
        foreach (var child in BaudHost.Children)
            if (child is RadioButton { IsChecked: true, Tag: int baud })
                return baud;
        return DefaultBaud;
    }

    private void RefreshPorts(string? preselect)
    {
        var ports = PortEnumerator.Enumerate();
        PortList.ItemsSource = ports;

        if (ports.Count == 0)
            return;

        PortInfo? match = null;
        if (!string.IsNullOrEmpty(preselect))
            match = ports.FirstOrDefault(p => string.Equals(p.PortName, preselect, StringComparison.OrdinalIgnoreCase));

        PortList.SelectedItem = match ?? ports[0];
        PortList.Focus();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        string? current = (PortList.SelectedItem as PortInfo)?.PortName ?? _sessionPort;
        _sessions?.Load(); // 다른 인스턴스/손편집 반영
        LoadSessions();
        RefreshPorts(current);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void PortList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PortList.SelectedItem is PortInfo) Accept();
    }

    private void Accept()
    {
        // 감지된 포트가 선택돼 있으면 그것을, 아니면 세션이 지정한(현재 없는) 포트명을 쓴다.
        PortInfo? port = PortList.SelectedItem as PortInfo;
        if (port is null && !string.IsNullOrEmpty(_sessionPort))
            port = new PortInfo(_sessionPort, null);

        if (port is null)
        {
            MessageBox.Show(this, "포트를 선택하세요.", "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPort = port;
        SelectedBaud = CheckedBaud();
        // 세션으로 접속한 경우에만 그룹을 넘긴다(포트 목록에서 직접 고른 경우 현재 그룹 유지).
        SelectedCommandGroup = string.Equals(port.PortName, _sessionPort, StringComparison.OrdinalIgnoreCase)
            ? _sessionCommandGroup : null;
        DialogResult = true;
    }
}
