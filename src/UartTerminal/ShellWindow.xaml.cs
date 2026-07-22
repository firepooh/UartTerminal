using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UartTerminal.Mcp;

namespace UartTerminal;

/// <summary>
/// 탭 호스트 창(Tier A + 화면 분할). 여러 UART 문서(<see cref="UartDocumentView"/>)를 탭 헤더로 관리하고,
/// 콘텐츠는 <c>ContentHost</c> 가 직접 렌더한다 — 탭 모드(활성 1개) 또는 분할 모드(격자 배치).
/// 문서는 TabItem.Tag 에 보관하며, 탭을 다른 창으로 옮기거나 분할해도 같은 프로세스 내 reparent 라
/// 시리얼 연결/ MCP 가 유지된다. 메인/떠다니는 창 모두 이 클래스의 인스턴스다.
/// </summary>
public partial class ShellWindow : Window
{
    private sealed class TabHooks
    {
        public required TextBlock HeaderText;
        public required Ellipse HeaderDot;
        public required Action Title;
        public required Action<string> Status;
        public required Action<string> Metrics;
        public required Action Mcp;
    }

    // GitHub-dark 팔레트(사용자 제공 디자인)
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x2F, 0x81, 0xF7));
    private static readonly Brush PanelBorderInactive = Frozen(Color.FromRgb(0x1C, 0x24, 0x33));
    private static readonly Brush PanelHeaderBg = Frozen(Color.FromRgb(0x0F, 0x14, 0x1D));
    private static readonly Brush ContentBg = Frozen(Color.FromRgb(0x0D, 0x11, 0x17));
    private static readonly Brush DotConnected = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush DotIdle = Frozen(Color.FromRgb(0x4B, 0x55, 0x63));
    private static readonly Brush DotReconnecting = Frozen(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly Brush TitleActiveFg = Frozen(Color.FromRgb(0xE6, 0xED, 0xF5));
    private static readonly Brush TitleInactiveFg = Frozen(Color.FromRgb(0x8B, 0x97, 0xA8));
    private static readonly Brush ConnectedFg = Frozen(Color.FromRgb(0xE6, 0xED, 0xF5));
    private static readonly Brush DisconnectedFg = Frozen(Color.FromRgb(0x8B, 0x97, 0xA8));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static ShellWindow? Primary { get; private set; }

    private readonly AppState _state;
    private readonly bool _isPrimary;
    private readonly Dictionary<TabItem, TabHooks> _hooks = new();

    private bool _splitMode;
    private bool _splitVertical = true; // 2분할 기본 = 좌/우(세로 경계)

    // 분할 렌더 시 패널 구성요소 참조(재렌더 없이 하이라이트/타이틀 갱신)
    private readonly Dictionary<UartDocumentView, Border> _panelBorders = new();
    private readonly Dictionary<UartDocumentView, TextBlock> _panelTitleTexts = new();
    private readonly Dictionary<UartDocumentView, Ellipse> _panelDots = new();

    public ShellWindow(AppState state, bool isPrimary)
    {
        InitializeComponent();
        _state = state;
        _isPrimary = isPrimary;
        if (isPrimary) Primary = this;

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;

        Tabs.PreviewMouseLeftButtonDown += Tabs_DragDown;
        Tabs.PreviewMouseMove += Tabs_DragMove;
        Tabs.PreviewMouseLeftButtonUp += Tabs_DragUp;
    }

    // ── 문서/탭 접근 ─────────────────────────────────────────────────────────────

    private static UartDocumentView? DocOf(TabItem ti) => ti.Tag as UartDocumentView;
    private UartDocumentView? ActiveDoc => (Tabs.SelectedItem as TabItem)?.Tag as UartDocumentView;
    private TabItem? TabOf(UartDocumentView doc) =>
        Tabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, doc));
    internal IEnumerable<UartDocumentView> AllDocs() =>
        Tabs.Items.OfType<TabItem>().Select(DocOf).Where(d => d is not null)!.Cast<UartDocumentView>();

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        MenuAutoReconnect.IsChecked = _state.AutoReconnect;
        if (_isPrimary)
        {
            RestoreWindowBounds();
            NewTab();
            if (Tabs.Items.Count == 0) Close();
        }
        else
        {
            CascadeFromPrimary();
        }
    }

    // ── 탭 생성/부착 ────────────────────────────────────────────────────────────

    private void NewTab()
    {
        var dlg = new PortSelectDialog(_state.LastPort) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedPort is not { } port)
            return;

        var doc = new UartDocumentView(_state);
        var ti = new TabItem { Tag = doc };
        AttachTab(ti, doc);
        Tabs.Items.Add(ti);
        Tabs.SelectedItem = ti;
        doc.ConnectTo(port);
        RenderContent();
        doc.FocusTerminal();
    }

    private void AttachTab(TabItem ti, UartDocumentView doc)
    {
        var (header, text, dot) = BuildHeader(ti);
        ti.Header = header;
        UpdateHeaderText(text, dot, doc);

        var hooks = new TabHooks
        {
            HeaderText = text,
            HeaderDot = dot,
            Title = () => { UpdateHeaderText(text, dot, doc); UpdatePanelTitle(doc); if (ReferenceEquals(ActiveDoc, doc)) RefreshChrome(); },
            Status = s => { if (ReferenceEquals(ActiveDoc, doc)) StatusText.Text = s; },
            Metrics = s => { if (ReferenceEquals(ActiveDoc, doc)) MetricsText.Text = s; },
            Mcp = () => { if (ReferenceEquals(ActiveDoc, doc)) RefreshMcpChrome(); },
        };
        doc.TitleChanged += hooks.Title;
        doc.StatusChanged += hooks.Status;
        doc.MetricsChanged += hooks.Metrics;
        doc.McpStateChanged += hooks.Mcp;
        _hooks[ti] = hooks;
    }

    private void DetachTabHooks(TabItem ti)
    {
        if (_hooks.TryGetValue(ti, out var h) && DocOf(ti) is { } doc)
        {
            doc.TitleChanged -= h.Title;
            doc.StatusChanged -= h.Status;
            doc.MetricsChanged -= h.Metrics;
            doc.McpStateChanged -= h.Mcp;
        }
        _hooks.Remove(ti);
    }

    private (FrameworkElement header, TextBlock text, Ellipse dot) BuildHeader(TabItem ti)
    {
        var panel = new DockPanel { LastChildFill = true };

        var close = new Button
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "탭 닫기",
            Style = (Style)FindResource("TabCloseButton"),
        };
        close.Click += (_, _) => CloseTab(ti);
        DockPanel.SetDock(close, Dock.Right);

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = DotIdle,
        };
        DockPanel.SetDock(dot, Dock.Left);

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        panel.Children.Add(close);
        panel.Children.Add(dot);
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

        return (panel, text, dot);
    }

    /// <summary>연결 상태 점 색: 연결됨=초록, 재연결 대기=호박, 끊김=회색.</summary>
    private static Brush DotFor(UartDocumentView doc) =>
        doc.IsConnected ? DotConnected : doc.IsReconnecting ? DotReconnecting : DotIdle;

    private static void UpdateHeaderText(TextBlock text, Ellipse dot, UartDocumentView doc)
    {
        text.Text = doc.Title;
        text.Foreground = doc.IsConnected ? ConnectedFg : DisconnectedFg;
        dot.Fill = DotFor(doc);
    }

    // ── 콘텐츠 렌더(탭/분할) ─────────────────────────────────────────────────────

    private void RenderContent()
    {
        var docs = AllDocs().ToList();
        foreach (var d in docs) DetachViewFromParent(d);
        ContentHost.Children.Clear();
        _panelBorders.Clear();
        _panelTitleTexts.Clear();
        _panelDots.Clear();

        if (docs.Count == 0) return;

        if (!_splitMode)
        {
            var doc = ActiveDoc ?? docs[0];
            DetachViewFromParent(doc);
            ContentHost.Children.Add(doc);
            return;
        }

        var (rows, cols) = ComputeLayout(docs.Count);
        var grid = new Grid();
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < docs.Count; i++)
        {
            var panel = BuildPanel(docs[i]);
            Grid.SetRow(panel, i / cols);
            Grid.SetColumn(panel, i % cols);
            grid.Children.Add(panel);
        }
        ContentHost.Children.Add(grid);
    }

    private Border BuildPanel(UartDocumentView doc)
    {
        bool active = ReferenceEquals(ActiveDoc, doc);
        var mono = (FontFamily)FindResource("MonoFont");

        var border = new Border
        {
            Margin = new Thickness(2),
            BorderThickness = new Thickness(active ? 2 : 1),
            BorderBrush = active ? AccentBrush : PanelBorderInactive,
            Background = ContentBg,
        };

        var dock = new DockPanel { LastChildFill = true };

        var dot = new Ellipse
        {
            Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = DotFor(doc),
        };
        var title = new TextBlock
        {
            Text = doc.Title,
            FontFamily = mono,
            FontSize = 11.5,
            FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
            Foreground = active ? TitleActiveFg : TitleInactiveFg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleInner.Children.Add(dot);
        titleInner.Children.Add(title);

        var titleBar = new Border
        {
            Background = PanelHeaderBg,
            Height = 26,
            BorderBrush = PanelBorderInactive,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleInner,
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        dock.Children.Add(titleBar);

        DetachViewFromParent(doc);
        dock.Children.Add(doc);

        border.Child = dock;
        border.PreviewMouseDown += (_, _) => ActivatePanel(doc);

        _panelBorders[doc] = border;
        _panelTitleTexts[doc] = title;
        _panelDots[doc] = dot;
        return border;
    }

    private (int rows, int cols) ComputeLayout(int n)
    {
        if (n <= 1) return (1, 1);
        if (n == 2) return _splitVertical ? (1, 2) : (2, 1);
        if (n == 3) return (1, 3);
        if (n == 4) return (2, 2);
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);
        return (rows, cols);
    }

    private void ActivatePanel(UartDocumentView doc)
    {
        var ti = TabOf(doc);
        if (ti is not null) Tabs.SelectedItem = ti; // 탭 동기화(SelectionChanged 에서 하이라이트/포커스)
        UpdateSplitHighlights();
        doc.FocusTerminal();
    }

    private void UpdateSplitHighlights()
    {
        foreach (var (doc, border) in _panelBorders)
        {
            bool active = ReferenceEquals(ActiveDoc, doc);
            border.BorderBrush = active ? AccentBrush : PanelBorderInactive;
            border.BorderThickness = new Thickness(active ? 2 : 1);
            if (_panelTitleTexts.TryGetValue(doc, out var tx))
            {
                tx.Foreground = active ? TitleActiveFg : TitleInactiveFg;
                tx.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
        }
    }

    private void UpdatePanelTitle(UartDocumentView doc)
    {
        if (_panelTitleTexts.TryGetValue(doc, out var tx)) tx.Text = doc.Title;
        if (_panelDots.TryGetValue(doc, out var dot)) dot.Fill = DotFor(doc);
    }

    private static void DetachViewFromParent(UartDocumentView doc)
    {
        switch (doc.Parent)
        {
            case Panel p: p.Children.Remove(doc); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
            case ContentPresenter cp: cp.Content = null; break;
        }
    }

    // ── 탭 닫기 ──────────────────────────────────────────────────────────────────

    private void CloseTab(TabItem ti)
    {
        var doc = DocOf(ti);
        DetachTabHooks(ti);
        if (doc is not null) DetachViewFromParent(doc);
        Tabs.Items.Remove(ti);
        doc?.CloseDocument();
        if (Tabs.Items.Count == 0) { Close(); return; }
        RenderContent();
        RefreshChrome();
    }

    private void CloseActiveTab()
    {
        if (Tabs.SelectedItem is TabItem ti) CloseTab(ti);
    }

    // ── 분리 / 합치기 ────────────────────────────────────────────────────────────

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
        if (DocOf(ti) is not { } doc) return;
        DetachTabHooks(ti);
        DetachViewFromParent(doc);
        Tabs.Items.Remove(ti);
        RenderContent();
        RefreshChrome();
        target.AdoptTab(ti, doc);
        if (Tabs.Items.Count == 0 && !_isPrimary) Close();
    }

    private void AdoptTab(TabItem ti, UartDocumentView doc)
    {
        AttachTab(ti, doc);
        Tabs.Items.Add(ti);
        Tabs.SelectedItem = ti;
        RenderContent();
        RefreshChrome();
        doc.FocusTerminal();
    }

    // ── 탭 전환 / 크롬 ───────────────────────────────────────────────────────────

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Tabs)) return;
        if (_splitMode) UpdateSplitHighlights();
        else RenderContent();
        RefreshChrome();
        ActiveDoc?.FocusTerminal();
    }

    private void RefreshChrome()
    {
        var doc = ActiveDoc;
        Title = doc is null ? "UartTerminal" : $"{doc.Title} - UartTerminal";
        StatusText.Text = doc?.StatusMessage ?? "";
        MetricsText.Text = doc?.MetricsMessage ?? "";
        ConnDot.Fill = doc is not null ? DotFor(doc) : DotIdle;
        MenuSplit.IsChecked = _splitMode;
        MenuAutoReconnect.IsChecked = _state.AutoReconnect;
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

    // ── 메뉴 ─────────────────────────────────────────────────────────────────────

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();
    private void Reconnect_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ReconnectViaDialog();
    private void Disconnect_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Disconnect();

    private void AutoReconnect_Click(object sender, RoutedEventArgs e)
    {
        _state.AutoReconnect = MenuAutoReconnect.IsChecked;
        _state.Save();
        // 설정은 전역(_state)이므로 열린 모든 창의 메뉴 체크를 동기화하고,
        // 끄는 경우 모든 창의 진행 중 자동 재연결 대기를 즉시 취소한다(플로팅 창 포함).
        foreach (var w in Application.Current.Windows.OfType<ShellWindow>())
        {
            w.MenuAutoReconnect.IsChecked = _state.AutoReconnect;
            if (!_state.AutoReconnect)
                foreach (var d in w.AllDocs()) d.CancelAutoReconnect();
        }
    }
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

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        _splitMode = MenuSplit.IsChecked;
        RenderContent();
        RefreshChrome();
        ActiveDoc?.FocusTerminal();
    }

    private void SplitOrient_Click(object sender, RoutedEventArgs e)
    {
        _splitVertical = !_splitVertical;
        StatusText.Text = _splitVertical ? "2분할: 좌/우" : "2분할: 상/하";
        if (_splitMode) RenderContent();
    }

    private void McpEnabled_Click(object sender, RoutedEventArgs e)
    { ActiveDoc?.McpSetEnabled(MenuMcpEnabled.IsChecked); RefreshMcpChrome(); }
    private void McpReadOnly_Click(object sender, RoutedEventArgs e)
    { ActiveDoc?.McpSetReadOnly(MenuMcpReadOnly.IsChecked); RefreshMcpChrome(); }
    private void McpCopyCmd_Click(object sender, RoutedEventArgs e) => ActiveDoc?.McpCopyCommand();

    // ── 탭 순서 변경(드래그) ─────────────────────────────────────────────────────

    private Point _dragStart;
    private TabItem? _dragTab;

    private void Tabs_DragDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        var ti = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (ti is not null && Tabs.Items.Contains(ti))
        {
            _dragTab = ti;
            _dragStart = e.GetPosition(Tabs);
        }
    }

    private void Tabs_DragMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!Tabs.Items.Contains(_dragTab)) { _dragTab = null; return; }

        var pos = e.GetPosition(Tabs);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;

        var over = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (over is null || ReferenceEquals(over, _dragTab) || !Tabs.Items.Contains(over)) return;

        int to = Tabs.Items.IndexOf(over);
        Tabs.Items.Remove(_dragTab);
        Tabs.Items.Insert(to, _dragTab);
        Tabs.SelectedItem = _dragTab;
        if (_splitMode) RenderContent();
    }

    private void Tabs_DragUp(object sender, MouseButtonEventArgs e) => _dragTab = null;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

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
        foreach (var ti in Tabs.Items.OfType<TabItem>().ToList())
        {
            DetachTabHooks(ti);
            DocOf(ti)?.CloseDocument();
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
