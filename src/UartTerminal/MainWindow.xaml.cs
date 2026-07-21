using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;
using UartTerminal.Rendering;

namespace UartTerminal;

public partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly SerialConnectionParams _params = SerialConnectionParams.Default;
    private readonly Encoding _txEncoding = new UTF8Encoding(false);

    private TerminalEngine? _engine;
    private TerminalView? _view;
    private ISerialSession? _session;
    private string _portName = "";
    private bool _connected;

    public MainWindow()
    {
        InitializeComponent();
        _state = AppState.Load();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RestoreWindowBounds();
        if (!ShowPortDialogAndConnect(_state.LastPort))
        {
            Close();
        }
    }

    // ── 연결 수명주기 ──────────────────────────────────────────────────────────

    private bool ShowPortDialogAndConnect(string? preselect)
    {
        var dlg = new PortSelectDialog(preselect) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedPort is { } port)
        {
            Connect(port);
            return true;
        }
        return false;
    }

    private void Connect(PortInfo port)
    {
        _portName = port.PortName;
        EnsureEngine();
        OpenSession();
        _state.LastPort = _portName;
        _state.Save();
    }

    private void EnsureEngine()
    {
        if (_engine is not null) return;

        _engine = new TerminalEngine(new UTF8Encoding(false), maxLines: 10_000);
        _engine.Respond = mem => _session?.Enqueue(mem); // DSR 등 응답 → TX

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
            session.Closed += OnSessionClosed;
            session.Open();

            _session = session;
            _connected = true;
            _engine!.ResetParsing();
            DiagLog.Info($"연결됨: {_portName} ({_params.Summary()})");
            UpdateTitle();
            SetStatus($"연결됨: {_portName}");
            _view?.Focus();
        }
        catch (UnauthorizedAccessException)
        {
            DiagLog.Warn($"포트 사용 중: {_portName}");
            _connected = false;
            UpdateTitle();
            SetStatus($"{_portName} 사용 중(다른 프로그램/창)");
            MessageBox.Show(this,
                $"{_portName} 을(를) 열 수 없습니다.\n다른 프로그램(또는 이 프로그램의 다른 창)이 사용 중일 수 있습니다.",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DiagLog.Exception("OpenSession", ex);
            _connected = false;
            UpdateTitle();
            SetStatus($"연결 실패: {ex.Message}");
            MessageBox.Show(this, $"{_portName} 연결 실패:\n{ex.Message}",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        // 시리얼 워커 스레드. 엔진이 버퍼 락으로 스레드 안전 보장.
        try { _engine!.Receive(data.Span); }
        catch (Exception ex) { DiagLog.Exception("Receive", ex); }
    }

    private void OnSessionClosed(SerialCloseReason reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _connected = false;
            _session = null;
            UpdateTitle();
            switch (reason)
            {
                case SerialCloseReason.DeviceRemoved:
                    DiagLog.Warn($"장치 분리됨: {_portName}");
                    SetStatus("장치 분리됨 — [터미널>재연결]로 다시 연결");
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

    // ── 입력 / TX ─────────────────────────────────────────────────────────────

    private void OnPreviewTextInput(object? sender, TextCompositionEventArgs e)
    {
        if (!_connected || string.IsNullOrEmpty(e.Text)) return;
        Send(_txEncoding.GetBytes(e.Text));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // UI 단축키 우선 처리
        if (mods == ModifierKeys.Control && e.Key == Key.Insert)
        { Copy_Click(null, null!); e.Handled = true; return; }
        if (mods == ModifierKeys.Shift && e.Key == Key.Insert)
        { DoPaste(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.End)
        { _view?.ScrollToEnd(); e.Handled = true; return; }
        if (e.Key == Key.PageUp && _view is not null)
        { _view.ScrollByRows(-(_view.Rows - 1)); e.Handled = true; return; }
        if (e.Key == Key.PageDown && _view is not null)
        { _view.ScrollByRows(_view.Rows - 1); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        { AdjustFont(+1); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        { AdjustFont(-1); e.Handled = true; return; }

        if (!_connected) return;

        // 메뉴 모드(메뉴 항목에 포커스)일 때는 화살표/Enter/Escape가 메뉴 탐색이므로 시리얼로 보내지 않음
        if (Keyboard.FocusedElement is System.Windows.Controls.MenuItem) return;

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
        _view?.ScrollToEnd(); // 입력 시 최신 위치로
    }

    // ── 메뉴/명령 ──────────────────────────────────────────────────────────────

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) { SetStatus("이미 연결됨"); return; }
        if (string.IsNullOrEmpty(_portName)) return;
        OpenSession();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => _session?.Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var text = _view?.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
            TrySetClipboard(text!);
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => DoPaste();

    private void DoPaste()
    {
        if (!_connected) return;
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        // 여러 줄 붙여넣기 확인(README A2). CR 전용 개행(구형 Mac 형식)도 포함해 검사.
        if (text.Contains('\n') || text.Contains('\r'))
        {
            var r = MessageBox.Show(this,
                "여러 줄을 붙여넣습니다. 전송할까요?", "UartTerminal",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
        }

        // 개행을 Transmit New-line(CR)로 정규화
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        Send(_txEncoding.GetBytes(text));
    }

    private void ClearScreen_Click(object sender, RoutedEventArgs e)
        => _engine?.Buffer.ClearScreen(_view?.Rows ?? 25); // 스크롤백 보존
    private void ClearBuffer_Click(object sender, RoutedEventArgs e)
        => _engine?.Buffer.Clear();                        // 스크롤백 포함 전체 삭제
    private void ScrollEnd_Click(object sender, RoutedEventArgs e) => _view?.ScrollToEnd();
    private void FontLarger_Click(object sender, RoutedEventArgs e) => AdjustFont(+1);
    private void FontSmaller_Click(object sender, RoutedEventArgs e) => AdjustFont(-1);

    private void AdjustFont(double delta)
    {
        if (_view is null) return;
        _view.FontSize += delta;
        _state.FontSize = _view.FontSize;
        _state.Save();
    }

    // ── 스크롤바 ───────────────────────────────────────────────────────────────

    private void OnScrollMetrics(ScrollMetrics m)
    {
        VScroll.Maximum = Math.Max(0, m.TotalLines - m.ViewportRows); // 마지막 페이지가 하단 정렬(오버스크롤 방지)
        VScroll.ViewportSize = m.ViewportRows;
        VScroll.LargeChange = m.ViewportRows;
        VScroll.Value = m.TopLine; // 프로그램 설정은 Scroll 이벤트를 발생시키지 않음
        MetricsText.Text = _connected
            ? $"{_portName}  {_params.Summary()}  ·  {_view?.Columns}×{_view?.Rows}  ·  UTF-8"
            : $"(연결 안 됨)  ·  {_view?.Columns}×{_view?.Rows}";
    }

    private void VScroll_Scroll(object sender, ScrollEventArgs e) => _view?.SetTopLine((int)e.NewValue);

    // ── 클립보드/상태/제목 ─────────────────────────────────────────────────────

    private void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { DiagLog.Warn($"클립보드 설정 실패: {ex.Message}"); }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateTitle()
    {
        string suffix = _connected ? "" : " [연결 안 됨]";
        Title = $"{_portName}:{_params.BaudRate} - UartTerminal{suffix}";
    }

    // ── 창 위치/크기 저장·복원 ─────────────────────────────────────────────────

    private void RestoreWindowBounds()
    {
        if (_state.WindowWidth is > 0 && _state.WindowHeight is > 0)
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;

            double w = Math.Min(_state.WindowWidth!.Value, vw);
            double h = Math.Min(_state.WindowHeight!.Value, vh);
            double left = _state.WindowLeft ?? Left;
            double top = _state.WindowTop ?? Top;

            left = Math.Max(vl, Math.Min(left, vl + vw - w));
            top = Math.Max(vt, Math.Min(top, vt + vh - h));

            Width = w; Height = h; Left = left; Top = top;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                _state.WindowLeft = Left;
                _state.WindowTop = Top;
                _state.WindowWidth = Width;
                _state.WindowHeight = Height;
            }
            if (_view is not null) _state.FontSize = _view.FontSize;
            _state.Save();
        }
        catch (Exception ex) { DiagLog.Warn($"종료 저장 실패: {ex.Message}"); }

        try { _session?.Close(); } catch { }
    }
}
