using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UartTerminal.Mcp;

namespace UartTerminal;

/// <summary>
/// 탭 호스트 창(Tier A). 여러 UART 문서(<see cref="UartDocumentView"/>)를 탭으로 담고,
/// 메뉴/상태바를 활성 탭에 연결한다. 탭을 다른 창으로 "분리"(TabItem 이동)하거나 "합치기"할 수 있으며,
/// 같은 프로세스 내 reparent 라서 이동 중에도 시리얼 연결/ MCP 가 유지된다.
/// 메인/떠다니는 창 모두 이 클래스의 인스턴스이며 <see cref="_isPrimary"/> 로 구분한다.
/// </summary>
public partial class ShellWindow : Window
{
    private sealed class TabHooks
    {
        public required TextBlock HeaderText;
        public required Action Title;
        public required Action<string> Status;
        public required Action<string> Metrics;
        public required Action Mcp;
    }

    public static ShellWindow? Primary { get; private set; }

    private readonly AppState _state;
    private readonly bool _isPrimary;
    private readonly Dictionary<TabItem, TabHooks> _hooks = new();

    public ShellWindow(AppState state, bool isPrimary)
    {
        InitializeComponent();
        _state = state;
        _isPrimary = isPrimary;
        if (isPrimary) Primary = this;

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private UartDocumentView? ActiveDoc => (Tabs.SelectedItem as TabItem)?.Content as UartDocumentView;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_isPrimary)
        {
            RestoreWindowBounds();
            NewTab();
            if (Tabs.Items.Count == 0) Close(); // 최초 포트 선택 취소 시 종료
        }
        else
        {
            CascadeFromPrimary();
        }
    }

    // ── 탭 생성/부착 ────────────────────────────────────────────────────────────

    /// <summary>포트 선택 다이얼로그 → 새 UART 탭 추가·연결. 취소 시 아무것도 하지 않음.</summary>
    private void NewTab()
    {
        var dlg = new PortSelectDialog(_state.LastPort) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedPort is not { } port)
            return;

        var doc = new UartDocumentView(_state);
        var ti = new TabItem { Content = doc };
        AttachTab(ti, doc);
        Tabs.Items.Add(ti);
        Tabs.SelectedItem = ti;
        doc.ConnectTo(port);
        doc.FocusTerminal();
    }

    private void AttachTab(TabItem ti, UartDocumentView doc)
    {
        var (header, text) = BuildHeader(ti);
        ti.Header = header;
        UpdateHeaderText(text, doc);

        var hooks = new TabHooks
        {
            HeaderText = text,
            Title = () => { UpdateHeaderText(text, doc); if (IsTabActive(ti)) RefreshChrome(); },
            Status = s => { if (IsTabActive(ti)) StatusText.Text = s; },
            Metrics = s => { if (IsTabActive(ti)) MetricsText.Text = s; },
            Mcp = () => { if (IsTabActive(ti)) RefreshMcpChrome(); },
        };
        doc.TitleChanged += hooks.Title;
        doc.StatusChanged += hooks.Status;
        doc.MetricsChanged += hooks.Metrics;
        doc.McpStateChanged += hooks.Mcp;
        _hooks[ti] = hooks;
    }

    private void DetachTabHooks(TabItem ti)
    {
        if (_hooks.TryGetValue(ti, out var h) && ti.Content is UartDocumentView doc)
        {
            doc.TitleChanged -= h.Title;
            doc.StatusChanged -= h.Status;
            doc.MetricsChanged -= h.Metrics;
            doc.McpStateChanged -= h.Mcp;
        }
        _hooks.Remove(ti);
    }

    private (FrameworkElement header, TextBlock text) BuildHeader(TabItem ti)
    {
        var panel = new DockPanel { LastChildFill = true };

        var close = new Button
        {
            Content = "×",
            Width = 18,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            ToolTip = "탭 닫기",
        };
        close.Click += (_, _) => CloseTab(ti);
        DockPanel.SetDock(close, Dock.Right);

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        panel.Children.Add(close);
        panel.Children.Add(text);

        var cm = new ContextMenu();
        var miDetach = new MenuItem { Header = "새 창으로 분리" };
        miDetach.Click += (_, _) => DetachTab(ti);
        var miMerge = new MenuItem { Header = "메인 창으로 합치기" };
        miMerge.Click += (_, _) => MergeTab(ti);
        var miClose = new MenuItem { Header = "탭 닫기" };
        miClose.Click += (_, _) => CloseTab(ti);
        cm.Items.Add(miDetach);
        cm.Items.Add(miMerge);
        cm.Items.Add(miClose);
        panel.ContextMenu = cm;

        return (panel, text);
    }

    private static void UpdateHeaderText(TextBlock text, UartDocumentView doc)
    {
        text.Text = doc.Title;
        text.Foreground = doc.IsConnected
            ? new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4))
            : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    }

    private bool IsTabActive(TabItem ti) => ReferenceEquals(Tabs.SelectedItem, ti);

    // ── 탭 닫기 ──────────────────────────────────────────────────────────────────

    private void CloseTab(TabItem ti)
    {
        var doc = ti.Content as UartDocumentView;
        DetachTabHooks(ti);
        Tabs.Items.Remove(ti);
        doc?.CloseDocument();
        if (Tabs.Items.Count == 0)
            Close(); // 창의 마지막 탭 → 창 닫기(메인이면 앱 종료)
    }

    private void CloseActiveTab()
    {
        if (Tabs.SelectedItem is TabItem ti) CloseTab(ti);
    }

    // ── 분리 / 합치기 (창 간 TabItem 이동) ───────────────────────────────────────

    private void DetachTab(TabItem ti)
    {
        if (Tabs.Items.Count < 2)
        {
            StatusText.Text = "분리할 탭이 하나뿐입니다";
            return;
        }
        var floatWin = new ShellWindow(_state, isPrimary: false);
        floatWin.Show();
        MoveTab(ti, floatWin);
        floatWin.Activate();
    }

    private void MergeTab(TabItem ti)
    {
        if (_isPrimary || Primary is null)
        {
            StatusText.Text = "이미 메인 창입니다";
            return;
        }
        MoveTab(ti, Primary);
        Primary.Activate();
    }

    private void MoveTab(TabItem ti, ShellWindow target)
    {
        if (ti.Content is not UartDocumentView doc) return;
        DetachTabHooks(ti);
        Tabs.Items.Remove(ti);
        target.AdoptTab(ti, doc);
        if (Tabs.Items.Count == 0 && !_isPrimary)
            Close();
    }

    private void AdoptTab(TabItem ti, UartDocumentView doc)
    {
        AttachTab(ti, doc);
        Tabs.Items.Add(ti);
        Tabs.SelectedItem = ti;
        RefreshChrome();
        doc.FocusTerminal();
    }

    // ── 탭 전환 / 크롬 갱신 ──────────────────────────────────────────────────────

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Tabs)) return;
        RefreshChrome();
        ActiveDoc?.FocusTerminal();
    }

    private void RefreshChrome()
    {
        var doc = ActiveDoc;
        Title = doc is null ? "UartTerminal" : $"{doc.Title} - UartTerminal";
        StatusText.Text = doc?.StatusMessage ?? "";
        MetricsText.Text = doc?.MetricsMessage ?? "";
        RefreshMcpChrome();
    }

    private void RefreshMcpChrome()
    {
        var doc = ActiveDoc;
        MenuMcpEnabled.IsChecked = doc?.McpEnabled ?? false;
        MenuMcpReadOnly.IsChecked = doc?.McpReadOnly ?? false;

        if (doc is null || !doc.McpEnabled)
        {
            McpStatusText.Text = "MCP: 꺼짐";
            McpStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
        else
        {
            string pipe = McpPipeServer.PipeNameFor(doc.PortName);
            McpStatusText.Text = doc.McpReadOnly ? $"MCP: 켜짐 (읽기 전용, {pipe})" : $"MCP: 켜짐 ({pipe})";
            McpStatusText.Foreground = new SolidColorBrush(doc.McpReadOnly
                ? Color.FromRgb(0xD7, 0xBA, 0x7D)
                : Color.FromRgb(0x6A, 0x99, 0x55));
        }
    }

    // ── 전역 단축키 ──────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Alt) != 0 && e.SystemKey == Key.N)
        { ActiveDoc?.ReconnectViaDialog(); e.Handled = true; return; }
        if ((mods & ModifierKeys.Alt) != 0 && e.SystemKey == Key.I)
        { ActiveDoc?.Disconnect(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.T)
        { NewTab(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.W)
        { CloseActiveTab(); e.Handled = true; return; }
    }

    // ── 메뉴(활성 탭으로 포워드) ─────────────────────────────────────────────────

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();
    private void Reconnect_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ReconnectViaDialog();
    private void Disconnect_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Disconnect();
    private void Detach_Click(object sender, RoutedEventArgs e) { if (Tabs.SelectedItem is TabItem ti) DetachTab(ti); }
    private void Merge_Click(object sender, RoutedEventArgs e) { if (Tabs.SelectedItem is TabItem ti) MergeTab(ti); }
    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseActiveTab();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Copy();
    private void Paste_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Paste();
    private void ClearScreen_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ClearScreen();
    private void ClearBuffer_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ClearBuffer();
    private void ScrollEnd_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ScrollEnd();
    private void FontLarger_Click(object sender, RoutedEventArgs e) => ActiveDoc?.AdjustFont(+1);
    private void FontSmaller_Click(object sender, RoutedEventArgs e) => ActiveDoc?.AdjustFont(-1);

    private void McpEnabled_Click(object sender, RoutedEventArgs e)
    { ActiveDoc?.McpSetEnabled(MenuMcpEnabled.IsChecked); RefreshMcpChrome(); }
    private void McpReadOnly_Click(object sender, RoutedEventArgs e)
    { ActiveDoc?.McpSetReadOnly(MenuMcpReadOnly.IsChecked); RefreshMcpChrome(); }
    private void McpCopyCmd_Click(object sender, RoutedEventArgs e) => ActiveDoc?.McpCopyCommand();

    // ── 창 위치/크기 ─────────────────────────────────────────────────────────────

    private void RestoreWindowBounds()
    {
        if (_state.WindowWidth is > 0 && _state.WindowHeight is > 0)
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            double w = Math.Min(_state.WindowWidth!.Value, vw);
            double h = Math.Min(_state.WindowHeight!.Value, vh);
            double left = _state.WindowLeft ?? Left, top = _state.WindowTop ?? Top;
            left = Math.Max(vl, Math.Min(left, vl + vw - w));
            top = Math.Max(vt, Math.Min(top, vt + vh - h));
            Width = w; Height = h; Left = left; Top = top;
        }
    }

    private void CascadeFromPrimary()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var baseWin = Primary ?? this;
        Left = baseWin.Left + 36;
        Top = baseWin.Top + 36;
        Width = baseWin.Width;
        Height = baseWin.Height;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 이 창의 모든 탭 문서 정리(세션/ MCP)
        foreach (var ti in Tabs.Items.OfType<TabItem>().ToList())
        {
            DetachTabHooks(ti);
            (ti.Content as UartDocumentView)?.CloseDocument();
        }

        if (_isPrimary)
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
                _state.Save();
            }
            catch (Exception ex) { DiagLog.Warn($"종료 저장 실패: {ex.Message}"); }
        }
    }
}
