using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly List<string> _history = new();
    private int _historyIndex;

    // 셸(창)이 상태바/제목/MCP 체크를 갱신하도록 알림
    public event Action? TitleChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? MetricsChanged;
    public event Action? McpStateChanged;

    public string PortName => _portName;
    public bool IsConnected => _connected;
    public bool McpEnabled => _bridge?.Enabled ?? false;
    public bool McpReadOnly => _bridge?.ReadOnly ?? false;
    public string StatusMessage { get; private set; } = "";
    public string MetricsMessage { get; private set; } = "";

    /// <summary>탭 헤더용 제목(포트 + 연결 상태).</summary>
    public string Title =>
        string.IsNullOrEmpty(_portName)
            ? "(새 연결)"
            : _connected ? _portName : $"{_portName} [끊김]";

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

    public void Disconnect() => _session?.Close();

    private void EnsureEngine()
    {
        if (_engine is not null) return;

        _engine = new TerminalEngine(new UTF8Encoding(false), maxLines: 10_000);
        _engine.Respond = mem => _session?.Enqueue(mem); // DSR 등 응답 → TX

        _bridge = new UartBridge(_engine);
        _mcpServer = new McpPipeServer(_bridge, _portName);
        RaiseMcpState();

        _view = new TerminalView(_engine.Buffer) { FontSize = _state.FontSize };
        _view.ScrollMetricsChanged += OnScrollMetrics;
        _view.AutoCopyRequested += TrySetClipboard;
        _view.PasteRequested += DoPaste;
        ViewHost.Child = _view;
    }

    private void OpenSession()
    {
        try
        {
            var session = new SerialPortSession(_portName, _params);
            session.DataReceived += OnDataReceived;
            session.Closed += reason => OnSessionClosed(session, reason);
            _bridge?.AttachSession(session);
            session.Open();

            _session = session;
            _connected = true;
            _engine!.ResetParsing();
            DiagLog.Info($"연결됨: {_portName} ({_params.Summary()})");
            RaiseTitle();
            SetStatus($"연결됨: {_portName}");
            _view?.Focus();
        }
        catch (UnauthorizedAccessException)
        {
            DiagLog.Warn($"포트 사용 중: {_portName}");
            _connected = false;
            _bridge?.DetachSession();
            RaiseTitle();
            SetStatus($"{_portName} 사용 중(다른 프로그램/창)");
            MessageBox.Show(OwnerWindow,
                $"{_portName} 을(를) 열 수 없습니다.\n다른 프로그램(또는 다른 창/탭)이 사용 중일 수 있습니다.",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DiagLog.Exception("OpenSession", ex);
            _connected = false;
            _bridge?.DetachSession();
            RaiseTitle();
            SetStatus($"연결 실패: {ex.Message}");
            MessageBox.Show(OwnerWindow, $"{_portName} 연결 실패:\n{ex.Message}",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_session is not null && !ReferenceEquals(_session, closed))
                return; // 이미 다른 세션으로 교체됨

            _connected = false;
            _session = null;
            _bridge?.DetachSession();
            RaiseTitle();
            switch (reason)
            {
                case SerialCloseReason.DeviceRemoved:
                    DiagLog.Warn($"장치 분리됨: {_portName}");
                    SetStatus("장치 분리됨 — Alt+N 또는 [터미널>재연결]");
                    break;
                case SerialCloseReason.UserClosed:
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

    private void InputBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        InputBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        InputLabel.Text = "입력창 (Enter=전송)";
        InputLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }

    private void InputBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        InputBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
        InputLabel.Text = "입력창";
        InputLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

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

    public void AdjustFont(double delta)
    {
        if (_view is null) return;
        _view.FontSize += delta;
        _state.FontSize = _view.FontSize;
        _state.Save();
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

    // ── 정리 ─────────────────────────────────────────────────────────────────────

    /// <summary>탭/창이 닫힐 때 세션·MCP 정리.</summary>
    public void CloseDocument()
    {
        try { _mcpServer?.Stop(); } catch { }
        try { _session?.Close(); } catch { }
    }

    // ── 스크롤바/상태/이벤트 ─────────────────────────────────────────────────────

    private void OnScrollMetrics(ScrollMetrics m)
    {
        VScroll.Maximum = Math.Max(0, m.TotalLines - m.ViewportRows);
        VScroll.ViewportSize = m.ViewportRows;
        VScroll.LargeChange = m.ViewportRows;
        VScroll.Value = m.TopLine;
        MetricsMessage = _connected
            ? $"{_portName}  {_params.Summary()}  ·  {_view?.Columns}×{_view?.Rows}  ·  UTF-8"
            : $"(연결 안 됨)  ·  {_view?.Columns}×{_view?.Rows}";
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
