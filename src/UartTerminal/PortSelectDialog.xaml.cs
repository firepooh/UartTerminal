using System.Windows;
using System.Windows.Controls;
using UartTerminal.Core.Config;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;

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

    /// <summary>세션 선택으로 지정된 포트(감지 목록에 없을 수도 있다 — 그 경우 자동 재연결 대기로 이어진다).</summary>
    private string? _sessionPort;

    /// <summary>세션 선택으로 지정된 명령 그룹(접속 확정 시 SelectedCommandGroup 으로 넘어간다).</summary>
    private string? _sessionCommandGroup;

    /// <summary>세션 선택으로 지정된 개행 규약(없으면 null → 현재 탭 값 유지).</summary>
    private ReceiveNewline? _sessionNewlineRx;
    private TransmitNewline? _sessionNewlineTx;

    /// <summary>세션이 '열 때 MCP 켜기'를 지정했는지. 명령 그룹과 같이 세션에서만 오는 값이라 폼에 컨트롤을 두지 않는다.</summary>
    private bool _sessionMcpOnOpen;

    /// <summary>세션 이름과 로그 폴더(로그 파일 기본 이름·저장 위치에 쓰인다).</summary>
    private string? _sessionName;
    private string? _sessionLogFolder;

    public PortInfo? SelectedPort { get; private set; }

    /// <summary>사용자가 고른 통신 속도. 취소 시 의미 없음.</summary>
    public int SelectedBaud { get; private set; } = DefaultBaud;

    /// <summary>이 연결에서 '열 때 보드 리셋'을 할지(세션에도 함께 저장되는 접속 속성). 취소 시 의미 없음.</summary>
    public bool SelectedResetOnOpen { get; private set; }

    /// <summary>세션으로 접속한 경우 그 세션에 연결된 명령 그룹(없으면 null → 그룹 자동 전환 안 함).</summary>
    public string? SelectedCommandGroup { get; private set; }

    /// <summary>
    /// 세션으로 접속한 경우 그 세션이 지정한 개행 규약. <b>null 이면 '지정 없음'</b> —
    /// 세션을 안 쓰고 포트만 골라 열었거나 세션에 값이 없을 때이며, 호출자는 현재 값을 그대로 유지한다.
    /// </summary>
    public ReceiveNewline? SelectedNewlineRx { get; private set; }
    public TransmitNewline? SelectedNewlineTx { get; private set; }

    /// <summary>
    /// 세션으로 접속한 경우 그 세션이 지정한 '열 때 MCP 서버 켜기'. 세션을 쓰지 않으면 항상 false —
    /// MCP 는 켜는 순간 이름 있는 파이프가 열리므로 <b>명시적으로 저장한 세션</b>으로만 자동 활성화한다.
    /// </summary>
    public bool SelectedMcpOnOpen { get; private set; }

    /// <summary>세션으로 접속한 경우 그 세션의 이름(로그 파일 이름의 첫 칸). 아니면 null.</summary>
    public string? SelectedSessionName { get; private set; }

    /// <summary>세션으로 접속한 경우 그 세션의 로그 폴더(로깅 다이얼로그가 미리 채운다). 아니면 null.</summary>
    public string? SelectedLogFolder { get; private set; }

    public PortSelectDialog(string? preselectPort = null, int preselectBaud = DefaultBaud,
                            SessionStore? sessions = null, bool preselectResetOnOpen = false)
    {
        InitializeComponent();
        _sessions = sessions;
        ResetOnOpenCheck.IsChecked = preselectResetOnOpen;
        SelectedResetOnOpen = preselectResetOnOpen;
        BuildBaudChips(preselectBaud);
        LoadSessions();
        RefreshPorts(preselectPort);
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
        _sessionNewlineRx = s.NewlineRx;       // 세션에 값이 있으면 접속 시 그 개행 규약으로 전환
        _sessionNewlineTx = s.NewlineTx;
        _sessionMcpOnOpen = s.McpOnOpen;        // 접속 후 이 탭의 MCP 서버를 자동으로 켠다
        _sessionName = s.Name;
        _sessionLogFolder = s.LogFolder;
        SetBaud(s.Baud);
        ResetOnOpenCheck.IsChecked = s.ResetOnOpen; // 보드마다 다른 값 — 세션이 폼을 채운다

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

        var r = MessageBox.Show(this, Loc.F("Sess.ConfirmDelete", s.Display), "UartTerminal",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return;

        if (!_sessions.Remove(s))
        {
            MessageBox.Show(this, Loc.FormatOrNull(_sessions.LastError) ?? Loc.S("Sess.DeleteFailed"),
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
            MessageBox.Show(this, Loc.S("Port.PickOne"), "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPort = port;
        SelectedBaud = CheckedBaud();
        SelectedResetOnOpen = ResetOnOpenCheck.IsChecked == true;
        // 세션으로 접속한 경우에만 세션 값을 넘긴다(포트 목록에서 직접 고른 경우 현재 값 유지).
        bool viaSession = string.Equals(port.PortName, _sessionPort, StringComparison.OrdinalIgnoreCase);
        SelectedCommandGroup = viaSession ? _sessionCommandGroup : null;
        SelectedNewlineRx = viaSession ? _sessionNewlineRx : null;
        SelectedNewlineTx = viaSession ? _sessionNewlineTx : null;
        SelectedMcpOnOpen = viaSession && _sessionMcpOnOpen;
        SelectedSessionName = viaSession ? _sessionName : null;
        SelectedLogFolder = viaSession ? _sessionLogFolder : null;
        DialogResult = true;
    }
}
