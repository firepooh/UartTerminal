using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly SerialConnectionParams _params = SerialConnectionParams.Default;
    private readonly Encoding _txEncoding = new UTF8Encoding(false);

    private TerminalEngine? _engine;
    private TerminalView? _view;
    private ISerialSession? _session;
    private UartBridge? _bridge;
    private McpPipeServer? _mcpServer;
    private string _portName = "";
    private bool _connected;

    // 자동 재연결(USB 재접속 감시)
    private DispatcherTimer? _reconnectTimer;
    private bool _reconnectPending;
    private string? _lastOpenError;
    private bool _closed; // 문서가 닫힘/폐기됨 — 지연된 Closed 콜백의 재무장을 차단
    private bool _mcpReleased; // AI(MCP)가 외부 작업(플래싱 등)을 위해 포트를 양보한 상태

    private readonly List<string> _history = new();
    private int _historyIndex;

    // 셸(창)이 상태바/제목/MCP 체크를 갱신하도록 알림
    public event Action? TitleChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? MetricsChanged;
    public event Action? McpStateChanged;

    public string PortName => _portName;
    public bool IsConnected => _connected;
    public bool IsReconnecting => _reconnectPending;
    public bool IsPortReleased => _mcpReleased;
    public bool McpEnabled => _bridge?.Enabled ?? false;
    public bool McpReadOnly => _bridge?.ReadOnly ?? false;
    public string StatusMessage { get; private set; } = "";
    public string MetricsMessage { get; private set; } = "";

    /// <summary>탭 헤더용 제목(포트 + 연결 상태).</summary>
    public string Title =>
        string.IsNullOrEmpty(_portName)
            ? "(새 연결)"
            : _connected ? _portName
            : _mcpReleased ? $"{_portName} [AI 양보]"
            : _reconnectPending ? $"{_portName} [재연결 중…]"
            : $"{_portName} [끊김]";

    public UartDocumentView(AppState state)
    {
        InitializeComponent();
        _state = state;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    // ── 연결 수명주기 ──────────────────────────────────────────────────────────

    /// <summary>최초 연결(엔진/뷰/브리지/MCP 서버 생성 후 세션 오픈).</summary>
    public void ConnectTo(PortInfo port)
    {
        _portName = port.PortName;
        EnsureEngine();
        OpenSession();
        _state.LastPort = _portName;
        _state.Save();
    }

    /// <summary>포트 리스트를 다시 보여주고 선택 포트로 재연결. 선택 확정 시에만 기존 세션을 닫는다.</summary>
    public void ReconnectViaDialog()
    {
        StopAutoReconnect(); // 사용자가 직접 재연결을 개시 — 자동 대기는 종료
        string? preselect = string.IsNullOrEmpty(_portName) ? _state.LastPort : _portName;
        var dlg = new PortSelectDialog(preselect) { Owner = OwnerWindow };
        if (dlg.ShowDialog() != true || dlg.SelectedPort is not { } port)
        {
            SetStatus("재연결 취소됨");
            return;
        }

        CloseCurrentSession();

        if (_engine is null)
        {
            ConnectTo(port);
            return;
        }

        if (!string.Equals(port.PortName, _portName, StringComparison.OrdinalIgnoreCase))
        {
            _portName = port.PortName;
            RebuildMcpForPort();
            RaiseTitle();
        }

        OpenSession();
        _state.LastPort = _portName;
        _state.Save();
    }

    public void Disconnect()
    {
        StopAutoReconnect();
        // 상태를 '즉시' 정리해 _session 을 비운다. 이렇게 하면 이미 큐잉된(지연된) DeviceRemoved 콜백이
        // 낡은 세션의 것이 되어 OnSessionClosed 가드에서 무시된다(사용자 해제 후 원치 않는 자동 재연결 방지).
        var s = _session;
        _session = null;
        _connected = false;
        _mcpReleased = false; // 사용자가 직접 해제 — 'AI 양보'가 아니라 '끊김' 상태로
        _bridge?.DetachSession();
        RaiseTitle();
        RefreshMetrics();
        SetStatus("연결 해제됨");
        if (s is not null) { try { s.Close(); } catch { } }
    }

    private void EnsureEngine()
    {
        if (_engine is not null) return;

        _engine = new TerminalEngine(new UTF8Encoding(false), maxLines: 10_000);
        _engine.Respond = mem => _session?.Enqueue(mem); // DSR 등 응답 → TX

        _bridge = new UartBridge(_engine);
        // uart_close/uart_open 은 MCP 서버 스레드에서 호출되므로 UI 스레드로 마샬링해 포트를 닫고/연다.
        _bridge.SetPortController(
            () => Dispatcher.InvokeAsync(McpReleasePort).Task,
            () => Dispatcher.InvokeAsync(McpReopenPort).Task);
        _mcpServer = new McpPipeServer(_bridge, _portName);
        RaiseMcpState();

        _view = new TerminalView(_engine.Buffer) { FontSize = _state.FontSize };
        _view.ScrollMetricsChanged += OnScrollMetrics;
        _view.AutoCopyRequested += TrySetClipboard;
        _view.PasteRequested += DoPaste;
        ViewHost.Child = _view;
    }

    private enum OpenOutcome { Success, InUse, Failed }

    /// <summary>세션 오픈 핵심(조용함: 상태 메시지/팝업/포커스 없음). 성공 시 _session/_connected 설정.</summary>
    private OpenOutcome TryOpenSessionCore()
    {
        var session = new SerialPortSession(_portName, _params);
        void OnClosedLocal(SerialCloseReason reason) => OnSessionClosed(session, reason);
        session.DataReceived += OnDataReceived;
        session.Closed += OnClosedLocal;
        _bridge?.AttachSession(session);
        try
        {
            session.Open();
        }
        catch (UnauthorizedAccessException)
        {
            DiscardFailedSession(session, OnClosedLocal);
            DiagLog.Warn($"포트 사용 중: {_portName}");
            RaiseTitle();
            return OpenOutcome.InUse;
        }
        catch (Exception ex)
        {
            DiscardFailedSession(session, OnClosedLocal);
            DiagLog.Exception("OpenSession", ex);
            _lastOpenError = ex.Message;
            RaiseTitle();
            return OpenOutcome.Failed;
        }

        _session = session;
        _connected = true;
        _mcpReleased = false; // 어떤 경로로든 열림에 성공하면 'AI 양보' 상태 해제
        _engine!.ResetParsing();
        DiagLog.Info($"연결됨: {_portName} ({_params.Summary()})");
        RaiseTitle();
        RefreshMetrics();
        return OpenOutcome.Success;
    }

    /// <summary>오픈 실패 세션 정리. Closed 구독을 먼저 끊어 Dispose 시 OnSessionClosed 오발화를 막는다.</summary>
    private void DiscardFailedSession(SerialPortSession session, Action<SerialCloseReason> onClosed)
    {
        session.Closed -= onClosed;
        session.DataReceived -= OnDataReceived;
        _connected = false;
        _bridge?.DetachSession();
        try { session.Dispose(); } catch { }
        RefreshMetrics(); // 실패(InUse/Failed) 전이 시 하단 메트릭도 '(연결 안 됨)'으로 갱신
    }

    /// <summary>사용자 개시 연결(성공 시 포커스, 실패 시 팝업). 진행 중 자동 재연결은 종료.</summary>
    private void OpenSession()
    {
        StopAutoReconnect();
        _mcpReleased = false; // 사용자 개시 연결 — 실패(InUse/Failed)해도 'AI 양보' 상태가 남지 않게
        switch (TryOpenSessionCore())
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
                SetStatus($"연결 실패: {_lastOpenError}");
                MessageBox.Show(OwnerWindow, $"{_portName} 연결 실패:\n{_lastOpenError}",
                    "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    // ── 자동 재연결(USB 재접속 감시) ─────────────────────────────────────────────
    // 장치 분리(DeviceRemoved) 후 같은 포트명이 다시 나타나는지 1.5초 주기로 폴링하다가
    // 나타나면 조용히 재오픈한다. 사용자 종료(UserClosed)에는 동작하지 않는다.
    // 참고: 재접속 시 OS 가 다른 COM 번호를 배정하면(드묾) 원래 이름으로는 감지되지 않는다.

    private void StartAutoReconnect()
    {
        if (_closed) return;
        _reconnectPending = true;
        if (_reconnectTimer is null)
        {
            _reconnectTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _reconnectTimer.Tick += ReconnectTick;
        }
        _reconnectTimer.Start();
        RaiseTitle();
        SetStatus($"장치 분리됨 — 자동 재연결 대기 중… ({_portName})");
        DiagLog.Info($"자동 재연결 대기 시작: {_portName}");
    }

    private void StopAutoReconnect()
    {
        if (!_reconnectPending && _reconnectTimer is null) return;
        bool was = _reconnectPending;
        _reconnectPending = false;
        _reconnectTimer?.Stop();
        if (was) RaiseTitle();
    }

    /// <summary>설정에서 자동 재연결을 끌 때 진행 중인 대기를 취소.</summary>
    public void CancelAutoReconnect()
    {
        if (!_reconnectPending) return;
        StopAutoReconnect();
        SetStatus("자동 재연결 꺼짐 — Alt+N 또는 [터미널>재연결]");
    }

    private void ReconnectTick(object? sender, EventArgs e)
    {
        // 전역 설정(_state.AutoReconnect)을 매 틱 재확인 — 다른 창에서 토글을 꺼도 다음 틱에 스스로 종료.
        if (_closed || !_reconnectPending || !_state.AutoReconnect || _connected
            || string.IsNullOrEmpty(_portName) || _engine is null)
        {
            StopAutoReconnect();
            return;
        }

        if (!PortEnumerator.PortExists(_portName))
            return; // 아직 안 나타남 — 계속 대기

        switch (TryOpenSessionCore())
        {
            case OpenOutcome.Success:
                StopAutoReconnect();
                SetStatus($"자동 재연결됨: {_portName}");
                DiagLog.Info($"자동 재연결됨: {_portName}");
                break;
            case OpenOutcome.InUse:
                SetStatus($"재연결 대기 중… ({_portName} 사용 중)");
                break;
            default:
                SetStatus($"재연결 대기 중… ({_portName} 준비 중)");
                break;
        }
    }

    private void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        try { _engine!.Receive(data.Span); }
        catch (Exception ex) { DiagLog.Exception("Receive", ex); }
    }

    private void OnSessionClosed(ISerialSession closed, SerialCloseReason reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 이 콜백이 '현재' 활성 세션의 것이 아니면(교체/사용자 해제/문서 폐기) 낡은 콜백이므로 무시.
            // 특히 USB 분리 시 Closed 는 DisposePortSafely(최대 1.5s) 이후 발생해 지연되므로,
            // 그 사이 사용자가 닫기/해제한 경우 여기서 재무장(StartAutoReconnect)을 반드시 차단해야 한다.
            if (_closed || !ReferenceEquals(closed, _session))
                return;

            _connected = false;
            _session = null;
            _bridge?.DetachSession();
            RaiseTitle();
            RefreshMetrics();
            switch (reason)
            {
                case SerialCloseReason.DeviceRemoved:
                    DiagLog.Warn($"장치 분리됨: {_portName}");
                    if (_state.AutoReconnect && !string.IsNullOrEmpty(_portName) && _engine is not null)
                        StartAutoReconnect();
                    else
                        SetStatus("장치 분리됨 — Alt+N 또는 [터미널>재연결]");
                    break;
                case SerialCloseReason.UserClosed:
                    StopAutoReconnect();
                    SetStatus("연결 해제됨");
                    break;
                default:
                    SetStatus("연결 종료(오류)");
                    break;
            }
        });
    }

    private void CloseCurrentSession()
    {
        var s = _session;
        if (s is null) return;
        _session = null;
        _connected = false;
        _bridge?.DetachSession();
        try { s.Close(); } catch { }
        RaiseTitle();
        RefreshMetrics();
    }

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
        if (!_connected || string.IsNullOrEmpty(e.Text)) return;
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

        if (!_connected) return;

        var bytes = KeyMap.Map(e.Key, mods);
        if (bytes is not null)
        {
            Send(bytes);
            e.Handled = true;
        }
    }

    private void Send(byte[] data)
    {
        _session?.Enqueue(data);
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
        if (!_connected) { SetStatus("연결되지 않음 — 입력 전송 불가"); return; }
        string line = InputBox.Text;
        Send(_txEncoding.GetBytes(line + "\r"));
        if (!string.IsNullOrEmpty(line))
        {
            _history.Add(line);
            if (_history.Count > 200) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
        InputBox.Clear();
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

    // ── 명령(셸 메뉴에서 호출) ──────────────────────────────────────────────────

    public void Copy()
    {
        var text = _view?.GetSelectedText();
        if (!string.IsNullOrEmpty(text)) TrySetClipboard(text!);
    }

    public void Paste() => DoPaste();

    private void DoPaste()
    {
        if (!_connected) return;
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        if (text.Contains('\n') || text.Contains('\r'))
        {
            var r = MessageBox.Show(OwnerWindow,
                "여러 줄을 붙여넣습니다. 전송할까요?", "UartTerminal",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
        }

        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        Send(_txEncoding.GetBytes(text));
    }

    public void ClearScreen() => _engine?.Buffer.ClearScreen(_view?.Rows ?? 25);
    public void ClearBuffer() => _engine?.Buffer.Clear();
    public void ScrollEnd() => _view?.ScrollToEnd();

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

    // ── MCP 포트 제어(uart_close / uart_open) ────────────────────────────────────
    // AI 가 외부 도구(esptool 등)에 포트를 양보/재점유하는 경로. UartBridge 델리게이트가
    // Dispatcher 로 마샬링해 아래 두 메서드를 항상 UI 스레드에서 실행한다.

    /// <summary>AI(MCP)가 외부 작업(플래싱 등)을 위해 포트를 양보. 자동 재연결을 억제하고 세션을 닫는다.</summary>
    private PortActionResult McpReleasePort()
    {
        if (_closed || _engine is null || string.IsNullOrEmpty(_portName))
            return new PortActionResult { Ok = false, Port = _portName, State = "error", Error = "no_port" };

        StopAutoReconnect(); // AI 가 명시적으로 닫음 — USB 감시 폴링이 포트를 도로 잡지 않게 중단
        var s = _session;
        // _session 을 먼저 비워 지연된 Closed 콜백이 낡은 것이 되어 OnSessionClosed 가드에서 무시되게 한다
        // (Disconnect 와 동일 방침 — 사용자 해제·장치 분리 콜백이 자동 재연결을 재무장하는 것 방지).
        _session = null;
        _connected = false;
        bool wasOpen = s is not null;
        _mcpReleased = true;
        _bridge?.DetachSession();
        RaiseTitle();
        RefreshMetrics();
        SetStatus($"AI가 포트 양보 — 외부 작업 대기 중… ({_portName})");
        DiagLog.Info($"MCP 포트 양보(uart_close): {_portName}");
        if (s is not null) { try { s.Close(); } catch { } } // 실제 포트 핸들 해제(외부 도구가 열 수 있도록)

        return new PortActionResult
        {
            Ok = true,
            Connected = false,
            Port = _portName,
            State = wasOpen ? "closed" : "already_closed",
        };
    }

    /// <summary>AI(MCP)가 양보했던(또는 끊긴) 포트를 다시 연다(외부 작업 종료 후).</summary>
    private PortActionResult McpReopenPort()
    {
        if (_closed || _engine is null || string.IsNullOrEmpty(_portName))
            return new PortActionResult { Ok = false, Port = _portName, State = "error", Error = "no_port" };

        if (_connected)
        {
            _mcpReleased = false;
            return new PortActionResult { Ok = true, Connected = true, Port = _portName, State = "already_open" };
        }

        switch (TryOpenSessionCore()) // 성공 시 내부에서 _mcpReleased=false 처리
        {
            case OpenOutcome.Success:
                StopAutoReconnect(); // 장치 분리 후 대기 중이었다면 함께 종료
                SetStatus($"AI가 포트 재연결(uart_open): {_portName}");
                DiagLog.Info($"MCP 포트 재연결(uart_open): {_portName}");
                return new PortActionResult { Ok = true, Connected = true, Port = _portName, State = "open" };
            case OpenOutcome.InUse:
                SetStatus($"재연결 대기 — {_portName} 아직 사용 중(외부 작업 진행 중?)");
                return new PortActionResult { Ok = false, Connected = false, Port = _portName, State = "in_use", Error = "in_use" };
            default:
                SetStatus($"재연결 실패: {_lastOpenError}");
                return new PortActionResult { Ok = false, Connected = false, Port = _portName, State = "error", Error = _lastOpenError ?? "open_failed" };
        }
    }

    // ── 정리 ─────────────────────────────────────────────────────────────────────

    /// <summary>탭/창이 닫힐 때 세션·MCP 정리.</summary>
    public void CloseDocument()
    {
        _closed = true; // 이후 도착하는 지연 Closed 콜백의 재무장을 차단
        StopAutoReconnect();
        var s = _session;
        _session = null;
        try { _mcpServer?.Stop(); } catch { }
        if (s is not null) { try { s.Close(); } catch { } }
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
        MetricsMessage = _connected
            ? $"{_portName}  {_params.Summary()}  ·  {_view?.Columns}×{_view?.Rows}{font}  ·  UTF-8"
            : $"(연결 안 됨)  ·  {_view?.Columns}×{_view?.Rows}{font}";
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
