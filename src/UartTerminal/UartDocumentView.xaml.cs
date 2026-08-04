using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UartTerminal.Core.Config;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;
using UartTerminal.Mcp;
using UartTerminal.Rendering;

namespace UartTerminal;

/// <summary>
/// 하나의 UART 세션을 담는 자족 단위(Tier A 탭 문서). 시리얼 세션·터미널 엔진·렌더러·MCP·입력창을
/// 모두 이 UserControl 이 소유하므로, 탭을 다른 창으로 옮겨도(같은 프로세스 내 reparent) 연결이 유지된다.
/// 창(ShellWindow)은 메뉴/상태바를 이 컨트롤의 메서드/이벤트에 연결한다.
/// </summary>
public partial class UartDocumentView : UserControl
{
    private readonly AppState _state;
    private readonly CommandStore _commands;
    private readonly SessionStore _sessions;

    // 연결 시 선택된 통신 속도를 담는다(readonly 아님). 수동 재연결·자동 재연결(TryOpenSessionCore)·
    // MCP uart_open 이 모두 이 필드를 쓰므로, 여기만 갱신하면 세 경로가 같은 속도로 일관되게 열린다.
    private SerialConnectionParams _params = SerialConnectionParams.Default;

    // '열 때 보드 리셋'·'개행 규약'도 속도와 같은 성격의 접속 속성(장치마다 다르다) → 탭이 소유하고
    // 세션에 저장된다. _state 의 같은 이름 값들은 '마지막으로 쓴 값'(새 탭 기본값)일 뿐이다 — LastBaud 와 같은 역할.
    private bool _resetOnOpen;
    private ReceiveNewline _nlRx = ReceiveNewline.CrLf;
    private TransmitNewline _nlTx = TransmitNewline.Cr;

    private readonly Encoding _txEncoding = new UTF8Encoding(false);

    private TerminalEngine? _engine;
    private TerminalView? _view;
    private ConnectionController? _conn; // 연결 수명주기 상태머신(UI 비의존) — 세션/재연결/양보 관리
    private UartBridge? _bridge;
    private McpPipeServer? _mcpServer;
    private string _portName = "";

    // 자동 재연결 폴링 타이머: 대기 '상태'는 컨트롤러가 소유하고, 실제 DispatcherTimer 는 호스트가 돌린다.
    private DispatcherTimer? _reconnectTimer;
    // 연결 감시 타이머: 연결 중 포트가 사라지면(유휴 케이블 뽑기) 감지해 컨트롤러에 알린다.
    private DispatcherTimer? _watchdogTimer;

    private readonly List<string> _history = new();
    private int _historyIndex;

    // 셸(창)이 상태바/제목/MCP 체크를 갱신하도록 알림
    public event Action? TitleChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? MetricsChanged;
    public event Action? McpStateChanged;

    public string PortName => _portName;
    public bool IsConnected => _conn?.IsConnected ?? false;
    public bool IsReconnecting => _conn?.IsReconnecting ?? false;
    public bool IsPortReleased => _conn?.IsPortReleased ?? false;
    public bool McpEnabled => _bridge?.Enabled ?? false;
    public bool McpReadOnly => _bridge?.ReadOnly ?? false;
    public string StatusMessage { get; private set; } = "";
    public string MetricsMessage { get; private set; } = "";

    /// <summary>탭 헤더용 제목(포트 + 연결 상태).</summary>
    public string Title
    {
        get
        {
            if (string.IsNullOrEmpty(_portName)) return "(새 연결)";
            if (_conn is null) return $"{_portName} [끊김]";
            if (_conn.IsConnected) return _portName;
            if (_conn.IsPortReleased) return $"{_portName} [AI 양보]";
            if (_conn.IsReconnecting) return $"{_portName} [재연결 중…]";
            return $"{_portName} [끊김]";
        }
    }

    public UartDocumentView(AppState state, CommandStore commands, SessionStore sessions)
    {
        InitializeComponent();
        _state = state;
        _commands = commands;
        _sessions = sessions;
        // 마지막으로 쓴 값을 새 탭의 기본값으로(세션 없이 열 때 이 값이 그대로 쓰인다)
        _resetOnOpen = state.ResetOnOpen;
        _nlRx = state.NewlineRx;
        _nlTx = state.NewlineTx;
        _params = MakeParams(state.LastBaud);
        _commands.Changed += OnCommandsChanged;
        SetCommandBarVisible(state.ShowCommandBar);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    /// <summary>
    /// 통신 속도 + '열 때 보드 리셋' 만 반영한 접속 파라미터. 나머지는 고정(README §2) —
    /// 오픈 시 DTR/RTS deassert 는 ESP32 의 의도치 않은 리셋/부트모드 진입을 막는 안전장치(R2)다.
    /// 리셋을 <b>원할 때</b>만 오픈 직후 EN 펄스를 주도록 ResetOnOpen 으로 분리했다.
    /// 범위를 벗어난 값(손편집된 state.json 등)은 기본값으로 되돌린다.
    /// </summary>
    private SerialConnectionParams MakeParams(int baud)
    {
        int b = baud is >= 300 and <= 4_000_000 ? baud : PortSelectDialog.DefaultBaud;
        return new SerialConnectionParams { BaudRate = b, ResetOnOpen = _resetOnOpen };
    }

    // ── 연결 수명주기 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 최초 연결(엔진/뷰/브리지/MCP 서버 생성 후 세션 오픈).
    /// resetOnOpen 은 이 연결의 속성이고, 개행은 <b>null 이면 지정 없음</b> → 현재(기본) 값을 유지한다.
    /// </summary>
    public void ConnectTo(PortInfo port, int baud, bool resetOnOpen = false,
                          ReceiveNewline? newlineRx = null, TransmitNewline? newlineTx = null)
    {
        _portName = port.PortName;
        _resetOnOpen = resetOnOpen;
        if (newlineRx is { } rx) _nlRx = rx;
        if (newlineTx is { } tx) _nlTx = tx;
        _params = MakeParams(baud);
        EnsureEngine();
        OpenSession();
        SaveConnectionDefaults();
    }

    /// <summary>다음 새 탭/다이얼로그의 기본값으로 쓰일 '마지막으로 쓴 접속 값'을 기록.</summary>
    private void SaveConnectionDefaults()
    {
        _state.LastPort = _portName;
        _state.LastBaud = _params.BaudRate;
        _state.ResetOnOpen = _resetOnOpen;
        _state.NewlineRx = _nlRx;
        _state.NewlineTx = _nlTx;
        _state.Save();
    }

    /// <summary>포트 리스트를 다시 보여주고 선택 포트로 재연결. 선택 확정 시에만 기존 세션을 닫는다.</summary>
    public void ReconnectViaDialog()
    {
        string? preselect = string.IsNullOrEmpty(_portName) ? _state.LastPort : _portName;
        // sessions:null — 재연결은 '지금 이 탭의 포트를 다시 고르는' 흐름이라 세션 목록을 띄우지 않는다(기존 동작 유지).
        var dlg = new PortSelectDialog(preselect, _params.BaudRate, sessions: null,
                                       preselectResetOnOpen: _resetOnOpen) { Owner = OwnerWindow };
        if (dlg.ShowDialog() != true || dlg.SelectedPort is not { } port)
        {
            // 취소는 아무것도 바꾸지 않아야 한다. 특히 진행 중인 자동 재연결 대기를 죽이면
            // USB 를 다시 꽂아도 되살아나지 않는다(사용자가 직접 재연결해야 함).
            SetStatus((_conn?.IsReconnecting ?? false)
                ? $"재연결 취소됨 — 자동 재연결 대기 계속 ({_portName})"
                : "재연결 취소됨");
            return;
        }

        SetCommandGroup(dlg.SelectedCommandGroup); // 세션으로 재접속했으면 그 그룹으로 전환
        CloseCurrentSession(); // OpenSession→OpenUserInitiated 이 자동 재연결 대기를 종료한다

        if (_engine is null)
        {
            ConnectTo(port, dlg.SelectedBaud, dlg.SelectedResetOnOpen,
                      dlg.SelectedNewlineRx, dlg.SelectedNewlineTx);
            return;
        }

        if (!string.Equals(port.PortName, _portName, StringComparison.OrdinalIgnoreCase))
        {
            _portName = port.PortName;
            RebuildMcpForPort();
            RaiseTitle();
        }
        // 속도/리셋 변경은 세션을 새로 여는 것으로 반영된다(위에서 기존 세션을 닫았고 아래에서 재오픈).
        _resetOnOpen = dlg.SelectedResetOnOpen;
        // 개행은 세션으로 접속한 경우에만 값이 오고, 아니면 이 탭의 값을 유지한다.
        if (dlg.SelectedNewlineRx is { } rx) SetReceiveNewline(rx);
        if (dlg.SelectedNewlineTx is { } tx) SetTransmitNewline(tx);
        _params = MakeParams(dlg.SelectedBaud);

        OpenSession();
        SaveConnectionDefaults();
        RefreshMetrics();
    }

    public void Disconnect() => _conn?.Disconnect();

    private void EnsureEngine()
    {
        if (_engine is not null) return;

        _engine = new TerminalEngine(new UTF8Encoding(false), maxLines: 10_000)
        {
            ReceiveNewline = _nlRx,
        };
        _bridge = new UartBridge(_engine) { TransmitNewline = _nlTx };

        // 연결 수명주기(세션/재연결/양보/지연콜백 무력화)는 UI 비의존 컨트롤러가 소유.
        // 호스트는 세션 생성 팩토리·UI 마샬링(post)·상태 알림(notify)·상태바만 배선한다.
        _conn = new ConnectionController(
            _engine, _bridge,
            // 파라미터를 열 때마다 새로 만든다 — '열 때 보드 리셋' 같은 전역 설정을 다른 창에서 바꿔도
            // 이 탭의 자동 재연결/MCP 재오픈까지 즉시 반영된다(캐시된 _params 로 인한 불일치 방지).
            factory: () => new SerialPortSession(_portName, MakeParams(_params.BaudRate)),
            portName: () => _portName,
            post: a => Dispatcher.BeginInvoke(a),
            autoReconnect: () => _state.AutoReconnect,
            notify: OnConnStateChanged,
            status: SetStatus);

        _engine.Respond = mem => // DSR 등 응답 → TX
        {
            if (DiagLog.Capture) DiagLog.Trace($"TX(resp)[{mem.Length}] {DiagLog.Escape(mem.Span)}");
            _conn.Enqueue(mem);
        };

        // uart_close/uart_open 은 MCP 서버 스레드에서 호출되므로 UI 스레드로 마샬링해 포트를 닫고/연다.
        _bridge.SetPortController(
            () => Dispatcher.InvokeAsync(_conn.McpRelease).Task,
            () => Dispatcher.InvokeAsync(_conn.McpReopen).Task);
        _mcpServer = new McpPipeServer(_bridge, _portName);
        RaiseMcpState();

        _view = new TerminalView(_engine.Buffer) { FontSize = _state.FontSize, ShowTimestamps = _state.ShowTimestamps };
        _view.ScrollMetricsChanged += OnScrollMetrics;
        _view.AutoCopyRequested += TrySetClipboard;
        _view.PasteRequested += DoPaste;
        ViewHost.Child = _view;
    }

    /// <summary>사용자 개시 연결(성공 시 포커스, 실패 시 팝업). 실제 상태 전이는 컨트롤러가 담당.</summary>
    private void OpenSession()
    {
        switch (_conn!.OpenUserInitiated())
        {
            case OpenOutcome.Success:
                SetStatus($"연결됨: {_portName}");
                _view?.Focus();
                break;
            case OpenOutcome.InUse:
                SetStatus($"{_portName} 사용 중(다른 프로그램/창)");
                MessageBox.Show(OwnerWindow,
                    $"{_portName} 을(를) 열 수 없습니다.\n다른 프로그램(또는 다른 창/탭)이 사용 중일 수 있습니다.",
                    "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            default:
                SetStatus($"연결 실패: {_conn.LastOpenError}");
                MessageBox.Show(OwnerWindow, $"{_portName} 연결 실패:\n{_conn.LastOpenError}",
                    "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    // ── 자동 재연결 폴링 타이머(대기 '상태'는 컨트롤러, 실제 타이머는 호스트) ──────
    // 컨트롤러가 IsReconnecting 을 켜면(장치 분리) 1.5초 주기로 tick 하며 포트 존재를 확인해 재오픈 시도.

    /// <summary>컨트롤러 상태 변경 알림 → 재연결/감시 타이머 동기화 + 제목/메트릭 갱신.</summary>
    private void OnConnStateChanged()
    {
        // 재연결 대기: 포트가 다시 나타나는지 폴링
        if (_conn is { IsReconnecting: true })
        {
            if (_reconnectTimer is null)
            {
                _reconnectTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(1500) };
                _reconnectTimer.Tick += (_, _) => _conn?.ReconnectTick(PortEnumerator.PortExists(_portName));
            }
            if (!_reconnectTimer.IsEnabled) _reconnectTimer.Start();
        }
        else
        {
            _reconnectTimer?.Stop();
        }

        // 연결 감시: 연결 중 포트가 목록에서 사라지면(유휴 케이블 뽑기) DeviceRemoved 로 취급
        if (_conn is { IsConnected: true })
        {
            if (_watchdogTimer is null)
            {
                _watchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(1500) };
                _watchdogTimer.Tick += (_, _) =>
                {
                    if (_conn is { IsConnected: true } && !PortEnumerator.PortExists(_portName))
                        _conn.HandlePortVanished();
                };
            }
            if (!_watchdogTimer.IsEnabled) _watchdogTimer.Start();
        }
        else
        {
            _watchdogTimer?.Stop();
        }

        RaiseTitle();
        RefreshMetrics();
    }

    /// <summary>설정에서 자동 재연결을 끌 때 진행 중인 대기를 취소.</summary>
    public void CancelAutoReconnect() => _conn?.CancelAutoReconnect();

    private void CloseCurrentSession() => _conn?.CloseCurrentSession();

    private void RebuildMcpForPort()
    {
        if (_bridge is null) return;
        bool wasEnabled = _bridge.Enabled;
        try { _mcpServer?.Stop(); } catch { }
        _mcpServer = new McpPipeServer(_bridge, _portName);
        if (wasEnabled)
        {
            _mcpServer.Start();
            DiagLog.Info($"MCP 파이프 변경: {McpPipeServer.PipeNameFor(_portName)} — 릴레이 재등록 필요");
            SetStatus($"포트 변경 — MCP 재등록 필요: [MCP] 메뉴 > 등록 명령 복사 ({_portName})");
        }
        RaiseMcpState();
    }

    // ── 입력 / TX ─────────────────────────────────────────────────────────────

    private void OnPreviewTextInput(object? sender, TextCompositionEventArgs e)
    {
        if (!IsConnected || string.IsNullOrEmpty(e.Text)) return;
        if (_view is null || !_view.IsKeyboardFocusWithin) return; // 메인 뷰 포커스일 때만 type-through
        Send(_txEncoding.GetBytes(e.Text));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // 창-레벨 단축키/타입-스루는 메인 터미널 뷰가 포커스일 때만(입력창/메뉴 포커스 시 그쪽에 위임)
        if (_view is null || !_view.IsKeyboardFocusWithin) return;

        if (mods == ModifierKeys.Control && e.Key == Key.Insert)
        { Copy(); e.Handled = true; return; }
        if (mods == ModifierKeys.Shift && e.Key == Key.Insert)
        { DoPaste(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.End)
        { _view.ScrollToEnd(); e.Handled = true; return; }
        if (e.Key == Key.PageUp)
        { _view.ScrollByRows(-(_view.Rows - 1)); e.Handled = true; return; }
        if (e.Key == Key.PageDown)
        { _view.ScrollByRows(_view.Rows - 1); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        { AdjustFont(+1); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        { AdjustFont(-1); e.Handled = true; return; }

        if (!IsConnected) return;

        var bytes = KeyMap.Map(e.Key, mods, _nlTx);
        if (bytes is not null)
        {
            Send(bytes);
            e.Handled = true;
        }
    }

    private void Send(byte[] data)
    {
        if (DiagLog.Capture) DiagLog.Trace($"TX[{data.Length}] {DiagLog.Escape(data)}");
        _conn?.Enqueue(data);
        _view?.ScrollToEnd();
    }

    // ── 하단 입력 전용 창 ───────────────────────────────────────────────────────

    // 입력 필드 테두리 강조는 CommandInput 스타일(포커스 시 accent)이 처리. 프롬프트만 밝게.
    private void InputBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Prompt.Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));

    private void InputBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Prompt.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));

    private void Send_Click(object sender, RoutedEventArgs e) => SendInputLine();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: SendInputLine(); e.Handled = true; break;
            case Key.Up: HistoryNav(-1); e.Handled = true; break;
            case Key.Down: HistoryNav(+1); e.Handled = true; break;
            case Key.Escape: InputBox.Clear(); _historyIndex = _history.Count; e.Handled = true; break;
        }
    }

    private void SendInputLine()
    {
        if (!IsConnected) { SetStatus("연결되지 않음 — 입력 전송 불가"); return; }
        SendLine(InputBox.Text);
        InputBox.Clear();
    }

    /// <summary>한 줄 전송 + 히스토리 적재. 입력창 전송과 칩 전송이 같은 경로를 쓰도록 분리(개행은 송신 설정값).</summary>
    private void SendLine(string line)
    {
        Send(_txEncoding.GetBytes(line + _nlTx.Text()));
        if (!string.IsNullOrEmpty(line))
        {
            _history.Add(line);
            if (_history.Count > 200) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
    }

    private void HistoryNav(int dir)
    {
        if (_history.Count == 0) return;
        int next = Math.Clamp(_historyIndex + dir, 0, _history.Count);
        if (next == _historyIndex) return;
        _historyIndex = next;
        InputBox.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    // ── 저장 명령 칩 바 ─────────────────────────────────────────────────────────
    // "버튼 = 한 줄 문자열 전송"까지만. 다단계 시퀀스/대기/조건은 MCP(uart_send/uart_expect)의 영역이다.

    /// <summary>이 탭이 쓰는 명령 그룹 이름(탭별 독립). 세션 접속 시 자동 설정된다.</summary>
    private string? _commandGroup;

    private void OnCommandsChanged() { SyncGroupSelector(); RebuildCommandChips(); }

    /// <summary>칩 바 표시/숨김(전역 설정, Alt+B). 숨기면 세로 픽셀을 전혀 쓰지 않는다.</summary>
    public void SetCommandBarVisible(bool show)
    {
        CommandBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { SyncGroupSelector(); RebuildCommandChips(); }
    }

    /// <summary>세션이 지정한 명령 그룹으로 전환(없으면 첫 그룹 유지).</summary>
    public void SetCommandGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return;
        _commandGroup = groupName;
        SyncGroupSelector();
        RebuildCommandChips();
        RefreshMetrics();
    }

    /// <summary>현재 선택된 그룹 이름(세션 저장 시 함께 기록).</summary>
    public string? CurrentCommandGroup =>
        _commands.FindGroup(_commandGroup)?.Name ?? (_commands.Groups.Count > 0 ? _commands.Groups[0].Name : null);

    private bool _syncingGroups; // SelectionChanged 재진입 방지

    private void SyncGroupSelector()
    {
        _syncingGroups = true;
        try
        {
            var names = _commands.GroupNames;
            GroupSelector.ItemsSource = names;
            // 그룹이 하나뿐이면 셀렉터를 감춰(기존 화면과 동일하게) 잡음을 줄인다.
            var vis = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            GroupSelector.Visibility = vis;
            GroupDivider.Visibility = vis;
            GroupLabel.Visibility = vis;
            string? cur = CurrentCommandGroup;
            GroupSelector.SelectedItem = cur;
            _commandGroup = cur;
        }
        finally { _syncingGroups = false; }
    }

    private void GroupSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGroups) return;
        _commandGroup = GroupSelector.SelectedItem as string;
        RebuildCommandChips();
        RefreshMetrics(); // 상태바의 CMD:그룹 표시 갱신
    }

    private void RebuildCommandChips()
    {
        if (CommandBar.Visibility != Visibility.Visible) return;
        ChipHost.Children.Clear();

        var items = _commands.CommandsOf(_commandGroup);
        if (items.Count == 0)
        {
            ChipHost.Children.Add(new TextBlock
            {
                Text = "저장된 명령이 없습니다 — 입력창에 명령을 쓰고 [+ 저장]",
                Foreground = (Brush)FindResource("TextFaint"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        var style = (Style)FindResource("ChipButton");
        foreach (var cmd in items)
        {
            var captured = cmd;
            var btn = new Button
            {
                Content = ChipCaption(cmd),
                Style = style,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Focusable = false, // 포커스를 가져가면 클릭 직후 타이핑이 어디로도 가지 않는다
                ToolTip = ChipTooltip(cmd),
                Tag = captured,
            };
            if (captured.IsFolder)
                btn.Click += (s, _) => ShowFolderMenu((Button)s!, captured);
            else
                btn.Click += (_, _) => RunSavedCommand(captured);
            ChipHost.Children.Add(btn);
        }
    }

    private static string ChipCaption(SavedCommand cmd) =>
        cmd.IsFolder ? cmd.Name + " ▾" : (cmd.Confirm ? "⚠ " + cmd.Name : cmd.Name);

    private static string ChipTooltip(SavedCommand cmd)
    {
        if (cmd.IsFolder)
            return $"{cmd.Name} — 하위 명령 {cmd.Items!.Count}개 (클릭해서 선택)";
        return cmd.Confirm
            ? $"{cmd.Text}\n(전송 전 확인 · Ctrl+클릭: 입력창에 채우기)"
            : $"{cmd.Text}\n(Ctrl+클릭: 입력창에 채우기)";
    }

    /// <summary>폴더 칩 클릭: 하위 명령 목록을 컨텍스트 메뉴로 띄워 고르게 한다(예: reset → sw/hw/wdt).</summary>
    private void ShowFolderMenu(Button anchor, SavedCommand folder)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var mono = (FontFamily)FindResource("MonoFont");
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            FontFamily = mono,
        };
        foreach (var sub in folder.Items!)
        {
            var captured = sub;
            var mi = new MenuItem
            {
                Header = captured.Confirm ? "⚠ " + captured.Name : captured.Name,
                FontFamily = mono,
                // 전송 문자열을 부제처럼 보여줘 어떤 명령인지 바로 알 수 있게 한다.
                InputGestureText = captured.Text,
                ToolTip = captured.Text,
            };
            // 폴더 칩을 Ctrl+클릭했으면 하위 선택도 '입력창에 채우기'로 동작(일관성).
            mi.Click += (_, _) => RunSavedCommand(captured, forceFill: ctrl);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>칩 클릭: 기본은 즉시 전송, Ctrl+클릭(또는 forceFill)은 입력창에 채우기(수정 후 전송).</summary>
    private void RunSavedCommand(SavedCommand cmd, bool forceFill = false)
    {
        if (forceFill || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            InputBox.Text = cmd.Text;
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
            return;
        }

        if (!IsConnected) { SetStatus("연결되지 않음 — 명령 전송 불가"); return; }

        if (cmd.Confirm)
        {
            // 기본 버튼을 취소로 둔다 — 스트레이 Enter 로 위험 명령(restart/erase)이 나가면 안 된다.
            var r = MessageBox.Show(OwnerWindow,
                $"이 명령을 전송할까요?\n\n{cmd.Text}", "UartTerminal",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (r != MessageBoxResult.OK) return;
        }

        SendLine(cmd.Text);
    }

    /// <summary>현재 입력창 내용을 명령으로 저장(휘발성 히스토리 → 영속 명령 승격 경로).</summary>
    public void SaveCurrentInputAsCommand()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            SetStatus("저장할 내용이 없습니다 — 입력창에 명령을 입력하세요");
            return;
        }
        // 저장 직전에 파일을 다시 읽어 외부 손편집/다른 인스턴스의 변경을 통째로 덮어쓰지 않게 한다.
        string? group = _commandGroup;
        _commands.Load();
        if (!_commands.Add(new SavedCommand { Name = text, Text = text }, group))
        {
            SetStatus(_commands.LastError ?? "명령 저장 실패");
            return;
        }
        SetStatus($"명령 저장됨: {text}" + (group is null ? "" : $" (그룹: {group})"));
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e) => SaveCurrentInputAsCommand();

    // ── 접속 프로필(세션) ────────────────────────────────────────────────────────

    /// <summary>현재 탭의 포트·속도를 이름 붙인 세션으로 저장(같은 이름이면 갱신).</summary>
    public void SaveCurrentAsSession()
    {
        if (string.IsNullOrEmpty(_portName))
        {
            SetStatus("저장할 연결이 없습니다");
            return;
        }

        // 기본 이름은 friendly name 이 아니라 포트명 — 사용자가 보드 별칭을 직접 붙이게 한다.
        string reset = _resetOnOpen ? " · 열 때 리셋" : "";
        string nl = _nlRx == ReceiveNewline.CrLf && _nlTx == TransmitNewline.Cr
            ? "" : $" · 개행 ↓{_nlRx.Label()} ↑{_nlTx.Label()}";
        string? name = TextPromptDialog.Ask(OwnerWindow, "세션 저장",
            $"이 연결({_portName} · {_params.BaudRate}bps{reset}{nl})을 어떤 이름으로 저장할까요?", _portName);
        if (name is null) return;

        string? group = CurrentCommandGroup; // 현재 탭이 쓰는 명령 그룹을 세션에 함께 기록(접속 시 자동 선택)
        _sessions.Load(); // 다른 인스턴스/손편집 반영 후 추가
        // 개행은 '현재 값'을 명시적으로 기록한다 — 나중에 기본값을 바꿔도 이 세션은 그대로 재현되게.
        if (!_sessions.AddOrReplace(new SessionProfile
        {
            Name = name,
            Port = _portName,
            Baud = _params.BaudRate,
            ResetOnOpen = _resetOnOpen,
            NewlineRx = _nlRx,
            NewlineTx = _nlTx,
            CommandGroup = group,
        }))
        {
            SetStatus(_sessions.LastError ?? "세션 저장 실패");
            return;
        }
        SetStatus($"세션 저장됨: {name} — {_portName} · {_params.BaudRate}{reset}{nl}");
    }

    private void EditCommands_Click(object sender, RoutedEventArgs e)
        => CommandEditDialog.ShowEditor(_commands, OwnerWindow);

    // ── 명령(셸 메뉴에서 호출) ──────────────────────────────────────────────────

    public void Copy()
    {
        var text = _view?.GetSelectedText();
        if (!string.IsNullOrEmpty(text)) TrySetClipboard(text!);
    }

    public void Paste() => DoPaste();

    private void DoPaste()
    {
        if (!IsConnected) return;
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        if (text.Contains('\n') || text.Contains('\r'))
        {
            var r = MessageBox.Show(OwnerWindow,
                "여러 줄을 붙여넣습니다. 전송할까요?", "UartTerminal",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (r != MessageBoxResult.OK) return;
        }

        // 클립보드의 개행(CRLF/LF/CR)을 송신 개행 설정값 하나로 정규화한다.
        string nl = _nlTx.Text();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", nl);
        Send(_txEncoding.GetBytes(text));
    }

    public void ClearScreen() => _engine?.Buffer.ClearScreen(_view?.Rows ?? 25);
    public void ClearBuffer() => _engine?.Buffer.Clear();
    public void ScrollEnd() => _view?.ScrollToEnd();

    /// <summary>현재 스크롤백 버퍼(논리 라인 전체)를 텍스트 파일로 1회 저장(사용자 개시). 연속 로깅과는 별개.</summary>
    public void SaveVisibleLog()
    {
        if (_engine is null) { SetStatus("저장할 내용이 없습니다"); return; }

        var sb = new StringBuilder();
        var buffer = _engine.Buffer;
        lock (buffer.SyncRoot)
        {
            int n = buffer.LineCount;
            for (int i = 0; i < n; i++)
            {
                sb.Append(buffer.GetLine(i).Text());
                if (i < n - 1) sb.Append('\n');
            }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string port = string.IsNullOrEmpty(_portName) ? "log" : _portName;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "버퍼 로그 저장",
            Filter = "텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
            FileName = $"UartTerminal-{port}-{stamp}.txt",
        };
        if (dlg.ShowDialog(OwnerWindow) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            SetStatus($"로그 저장됨: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            DiagLog.Exception("SaveVisibleLog", ex);
            SetStatus($"로그 저장 실패: {ex.Message}");
        }
    }

    /// <summary>폰트 크기 조절(Ctrl+± / Ctrl+휠). 6~48pt 로 clamp, 크기를 잠깐 오버레이로 표시.</summary>
    public void AdjustFont(double delta)
    {
        if (_view is null) return;
        double next = Math.Clamp(_view.FontSize + delta, 6, 48);
        if (Math.Abs(next - _view.FontSize) >= 0.01)
        {
            _view.FontSize = next;
            _state.FontSize = next;
            RefreshMetrics();
        }
        ShowZoomIndicator(next); // 한계에 도달해 크기가 안 바뀌어도 현재 크기는 표시
    }

    // 폰트 크기 오버레이 + 상태 저장 디바운스(줌 제스처가 끝난 뒤 1회 저장).
    private DispatcherTimer? _zoomTimer;

    private void ShowZoomIndicator(double size)
    {
        ZoomText.Text = $"{size:0.#} pt";
        ZoomIndicator.Visibility = Visibility.Visible;
        if (_zoomTimer is null)
        {
            _zoomTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            _zoomTimer.Tick += (_, _) =>
            {
                _zoomTimer!.Stop();
                ZoomIndicator.Visibility = Visibility.Collapsed;
                try { _state.Save(); } catch { } // 연속 휠 중 매번 디스크 쓰기 대신 제스처 종료 시 1회
            };
        }
        _zoomTimer.Stop();
        _zoomTimer.Start();
    }

    public void FocusTerminal() => _view?.Focus();

    /// <summary>라인별 수신 타임스탬프 표시 토글(전역 설정).</summary>
    public void SetTimestamps(bool on) { if (_view is not null) _view.ShowTimestamps = on; }

    // ── 개행 규약 / 제어선(ESP32 리셋·부트로더) ─────────────────────────────────

    /// <summary>이 탭의 개행 규약(세션에 저장되는 접속 속성).</summary>
    public ReceiveNewline NewlineRx => _nlRx;
    public TransmitNewline NewlineTx => _nlTx;

    /// <summary>
    /// 이 탭의 수신 개행 규약 변경(다음 수신분부터 적용 — 이미 그려진 화면은 바꾸지 않는다).
    /// 마지막으로 쓴 값도 갱신해 새 탭의 기본값이 되게 한다.
    /// </summary>
    public void SetReceiveNewline(ReceiveNewline mode)
    {
        _nlRx = mode;
        if (_engine is not null) _engine.ReceiveNewline = mode;
        _state.NewlineRx = mode;
        _state.Save();
        RefreshMetrics();
    }

    /// <summary>이 탭의 송신 개행 규약 변경(키 입력·입력바·칩·붙여넣기·AI 전송 모두 이 값을 쓴다).</summary>
    public void SetTransmitNewline(TransmitNewline mode)
    {
        _nlTx = mode;
        if (_bridge is not null) _bridge.TransmitNewline = mode;
        _state.NewlineTx = mode;
        _state.Save();
        RefreshMetrics();
    }

    /// <summary>이 탭의 '열 때 보드 리셋' 값(세션에 저장되는 접속 속성).</summary>
    public bool ResetOnOpen => _resetOnOpen;

    /// <summary>
    /// 이 탭의 '열 때 보드 리셋'을 바꾼다(다음 오픈부터 적용 — 지금 연결을 리셋하려면 Alt+R).
    /// 마지막으로 쓴 값도 갱신해 새 탭/다이얼로그의 기본값이 되게 한다.
    /// </summary>
    public void SetResetOnOpen(bool on)
    {
        _resetOnOpen = on;
        _params = MakeParams(_params.BaudRate);
        _state.ResetOnOpen = on;
        _state.Save();
        SetStatus(on
            ? "열 때 보드 리셋: 켜짐 — 다음 연결부터 적용(지금 리셋은 Alt+R)"
            : "열 때 보드 리셋: 꺼짐");
        RefreshMetrics();
    }


    // 리셋 시퀀스는 100ms+50ms 대기가 있어 비동기다. 중복 실행(연타)을 막는 가드.
    private bool _resetting;

    /// <summary>하드웨어 리셋(EN 펄스) — 보드를 재부팅한다.</summary>
    public Task HardResetAsync() => RunControlSequenceAsync(EspResetSequence.HardReset, "하드웨어 리셋");

    /// <summary>부트로더(다운로드 모드) 진입 — IO0=LOW 상태로 리셋을 해제한다.</summary>
    public Task EnterBootloaderAsync() => RunControlSequenceAsync(EspResetSequence.Bootloader, "부트로더 진입");

    private async Task RunControlSequenceAsync(IReadOnlyList<ControlLineStep> steps, string what)
    {
        if (_conn is null || !IsConnected)
        {
            SetStatus($"{what} 불가 — 연결되지 않음");
            return;
        }
        if (_resetting) return;

        _resetting = true;
        SetStatus($"{what}…");
        try
        {
            bool ok = await _conn.ApplyControlLinesAsync(steps);
            SetStatus(ok ? $"{what} 완료 ({_portName})" : $"{what} 실패 — 연결 상태 확인");
        }
        catch (Exception ex)
        {
            DiagLog.Exception(what, ex);
            SetStatus($"{what} 실패: {ex.Message}");
        }
        finally { _resetting = false; }
    }

    // ── 스크롤백 검색(Ctrl+F) ──────────────────────────────────────────────────
    // 논리 라인 버퍼에서 대소문자 무시 부분일치를 찾아 하이라이트하고 이전/다음으로 이동한다.
    // 매치는 검색 시점에 계산(절대 라인 번호로 저장 → 트림에도 안정). 새 수신분은 재검색 시 반영.

    private readonly List<TerminalView.SearchHit> _findHits = new();
    private int _findIndex = -1;

    /// <summary>찾기 바 열기(Ctrl+F). 기존 검색어가 있으면 재실행.</summary>
    public void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (!string.IsNullOrEmpty(FindBox.Text)) RunSearch();
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _findHits.Clear();
        _findIndex = -1;
        _view?.ClearSearch();
        FocusTerminal();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();
    private void FindNext_Click(object sender, RoutedEventArgs e) => MoveFind(+1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => MoveFind(-1);
    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                MoveFind((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    private void RunSearch()
    {
        _findHits.Clear();
        string q = FindBox.Text;
        if (_engine is not null && !string.IsNullOrEmpty(q))
        {
            var buffer = _engine.Buffer;
            lock (buffer.SyncRoot)
            {
                long trimmed = buffer.TrimmedCount;
                int n = buffer.LineCount;
                for (int i = 0; i < n; i++)
                {
                    string text = buffer.GetLine(i).Text();
                    int idx = 0;
                    while ((idx = text.IndexOf(q, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        _findHits.Add(new TerminalView.SearchHit(trimmed + i, idx, q.Length));
                        idx += q.Length; // 겹침 없이 다음
                    }
                }
            }
        }
        _findIndex = _findHits.Count > 0 ? 0 : -1;
        ApplyFind(scroll: _findIndex >= 0);
    }

    private void MoveFind(int dir)
    {
        if (_findHits.Count == 0) return;
        _findIndex = ((_findIndex < 0 ? 0 : _findIndex) + dir % _findHits.Count + _findHits.Count) % _findHits.Count;
        ApplyFind(scroll: true);
    }

    private void ApplyFind(bool scroll)
    {
        _view?.SetSearch(_findHits, _findIndex);
        FindCount.Text = _findHits.Count == 0
            ? (FindBox.Text.Length == 0 ? "" : "없음")
            : $"{_findIndex + 1}/{_findHits.Count}";
        if (scroll && _findIndex >= 0) _view?.ScrollLineIntoView(_findHits[_findIndex].AbsLine);
    }

    // ── MCP ─────────────────────────────────────────────────────────────────────

    public void McpSetEnabled(bool on)
    {
        if (_bridge is null || _mcpServer is null) return;
        _bridge.Enabled = on;
        if (on) _mcpServer.Start(); else _mcpServer.Stop();
        DiagLog.Info($"MCP {(on ? "활성화" : "비활성화")}: {McpPipeServer.PipeNameFor(_portName)}");
        RaiseMcpState();
    }

    public void McpSetReadOnly(bool ro)
    {
        if (_bridge is null) return;
        _bridge.ReadOnly = ro;
        RaiseMcpState();
    }

    public void McpCopyCommand()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "UartTerminal.McpRelay.exe");
        string name = $"uart-{_portName.ToLowerInvariant()}";
        string cmd = $"claude mcp add {name} -- \"{exe}\" {_portName}";
        TrySetClipboard(cmd);
        SetStatus("MCP 등록 명령을 클립보드에 복사했습니다");
    }

    // ── 정리 ─────────────────────────────────────────────────────────────────────

    /// <summary>탭/창이 닫힐 때 세션·MCP 정리. 세션 폐기·지연콜백 차단은 컨트롤러가 담당.</summary>
    public void CloseDocument()
    {
        _commands.Changed -= OnCommandsChanged; // 전역 스토어 구독 해지(닫힌 문서가 갱신을 붙잡지 않게)
        _reconnectTimer?.Stop();
        _watchdogTimer?.Stop();
        _conn?.CloseDocument();
        try { _mcpServer?.Stop(); } catch { }
    }

    // ── 스크롤바/상태/이벤트 ─────────────────────────────────────────────────────

    private void OnScrollMetrics(ScrollMetrics m)
    {
        VScroll.Maximum = Math.Max(0, m.TotalLines - m.ViewportRows);
        VScroll.ViewportSize = m.ViewportRows;
        VScroll.LargeChange = m.ViewportRows;
        VScroll.Value = m.TopLine;
        RefreshMetrics();
    }

    /// <summary>하단 메트릭 문자열을 현재 연결 상태 기준으로 다시 만들어 통지(연결/분리 전이 시에도 갱신되도록).</summary>
    private void RefreshMetrics()
    {
        string font = _view is null ? "" : $"  ·  {_view.FontSize:0.#}pt";
        // 명령 그룹이 여러 개일 때만 표시 — 지금 어느 세트가 적용됐는지 드롭다운을 열지 않아도 알 수 있게.
        string group = _commands.Groups.Count > 1 && CurrentCommandGroup is { } g ? $"  ·  CMD:{g}" : "";
        // 개행 규약은 기본값(RX CR+LF / TX CR)이 아닐 때만 표시 — 바꿔 놓은 걸 잊고 헤매지 않게.
        bool nlDefault = _nlRx == ReceiveNewline.CrLf && _nlTx == TransmitNewline.Cr;
        string nl = nlDefault ? "" : $"  ·  NL↓{_nlRx.Label()} ↑{_nlTx.Label()}";
        // 이 탭이 '열 때 리셋'인지 — 세션마다 다른 값이라 켜져 있을 때 상태바에 남긴다.
        string rst = _resetOnOpen ? "  ·  열 때 리셋" : "";
        MetricsMessage = IsConnected
            ? $"{_portName}  {_params.Summary()}{rst}  ·  {_view?.Columns}×{_view?.Rows}{font}{group}{nl}  ·  UTF-8"
            : $"(연결 안 됨){rst}  ·  {_view?.Columns}×{_view?.Rows}{font}{group}{nl}";
        MetricsChanged?.Invoke(MetricsMessage);
    }

    private void VScroll_Scroll(object sender, ScrollEventArgs e) => _view?.SetTopLine((int)e.NewValue);

    private void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { DiagLog.Warn($"클립보드 설정 실패: {ex.Message}"); }
    }

    private void SetStatus(string text)
    {
        StatusMessage = text;
        StatusChanged?.Invoke(text);
    }

    private void RaiseTitle() => TitleChanged?.Invoke();
    private void RaiseMcpState() => McpStateChanged?.Invoke();
}
