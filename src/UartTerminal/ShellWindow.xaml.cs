using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UartTerminal.Core.Config;
using UartTerminal.Core.Terminal;
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

    // 색은 테마(DarkTheme.xaml)에만 둔다. 예전에는 같은 팔레트를 여기 복사해 놨는데,
    // 테마를 고쳐도 이쪽은 옛 색을 계속 써서 패널 경계·상태 점이 배경에 묻어 있었다.
    // 프로퍼티로 매번 조회하므로 테마를 바꾸면 다음 렌더부터 새 색이 적용된다.
    private static Brush AccentBrush => Theme.Brush("Accent");
    private static Brush PanelBorderInactive => Theme.Brush("PanelBorderInactive");
    private static Brush PanelHeaderBg => Theme.Brush("PanelHeaderBg");
    private static Brush ContentBg => Theme.Brush("ContentBg");
    private static Brush DotConnected => Theme.Brush("DotConnected");
    private static Brush DotIdle => Theme.Brush("DotIdle");
    private static Brush DotReconnecting => Theme.Brush("DotReconnecting");
    private static Brush DotReleased => Theme.Brush("DotReleased");
    private static Brush SplitterBrush => Theme.Brush("SplitterBrush");
    private static Brush TitleActiveFg => Theme.Brush("TitleActiveFg");
    private static Brush TitleInactiveFg => Theme.Brush("TitleInactiveFg");
    private static Brush ConnectedFg => Theme.Brush("TitleActiveFg");
    private static Brush DisconnectedFg => Theme.Brush("TitleInactiveFg");

    public static ShellWindow? Primary { get; private set; }

    private readonly AppState _state;
    private readonly CommandStore _commands;
    private readonly SessionStore _sessions;
    private readonly bool _isPrimary;
    private readonly Dictionary<TabItem, TabHooks> _hooks = new();

    // 화면 분할 레이아웃(툴바 아이콘/메뉴와 1:1). Single=분할 안 함.
    private enum SplitLayout { Single, Columns, Rows, Grid }
    private SplitLayout _split = SplitLayout.Single;
    private bool IsSplit => _split != SplitLayout.Single;

    // 분할 렌더 시 패널 구성요소 참조(재렌더 없이 하이라이트/타이틀 갱신)
    private readonly Dictionary<UartDocumentView, Border> _panelBorders = new();
    private readonly Dictionary<UartDocumentView, TextBlock> _panelTitleTexts = new();
    private readonly Dictionary<UartDocumentView, Ellipse> _panelDots = new();

    public ShellWindow(AppState state, CommandStore commands, SessionStore sessions, bool isPrimary)
    {
        InitializeComponent();
        _state = state;
        _commands = commands;
        _sessions = sessions;
        _isPrimary = isPrimary;
        if (isPrimary) Primary = this;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        // 정적 이벤트 구독 — OnClosing 에서 반드시 해지한다(창이 닫혀도 살아남으면 누수).
        Loc.Changed += OnLanguageChanged;

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
        MenuCommandBar.IsChecked = _state.ShowCommandBar;
        MenuDiagCapture.IsChecked = _state.DiagCapture;
        MenuTimestamps.IsChecked = _state.ShowTimestamps;
        MenuResetOnOpen.IsChecked = _state.ResetOnOpen;
        SyncNewlineChrome();
        SyncThemeChrome();
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
        var dlg = new PortSelectDialog(_state.LastPort, _state.LastBaud, _sessions,
                                       preselectResetOnOpen: _state.ResetOnOpen) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedPort is not { } port)
            return;

        var doc = new UartDocumentView(_state, _commands, _sessions);
        var ti = new TabItem { Tag = doc };
        AttachTab(ti, doc);
        Tabs.Items.Add(ti);
        Tabs.SelectedItem = ti;
        doc.ConnectTo(port, dlg.SelectedBaud, dlg.SelectedResetOnOpen,
                      dlg.SelectedNewlineRx, dlg.SelectedNewlineTx);
        // 그룹 전환은 연결 <b>뒤에</b> — 앞에 두면 "세션의 그룹이 없다" 는 안내가
        // 곧바로 오는 "연결됨" 상태 메시지에 덮여 사용자가 볼 기회가 없다.
        doc.SetCommandGroup(dlg.SelectedCommandGroup); // 세션에 연결된 명령 그룹으로 자동 전환
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
            Style = (Style)FindResource("TabCloseButton"),
        };
        close.SetBinding(ToolTipProperty, Loc.Bind("Tip.CloseTab")); // 대입하면 탭 생성 시 언어로 굳는다
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

        // Header 는 바인딩으로 걸어 언어 전환을 따라오게 한다(대입하면 그 시점 언어로 굳는다).
        var cm = new ContextMenu();
        var miDetach = new MenuItem();
        miDetach.SetBinding(MenuItem.HeaderProperty, Loc.Bind("Ctx.Detach"));
        miDetach.Click += (_, _) => DetachTab(ti);
        var miMerge = new MenuItem();
        miMerge.SetBinding(MenuItem.HeaderProperty, Loc.Bind("Ctx.Merge"));
        miMerge.Click += (_, _) => MergeTab(ti);
        var miClose = new MenuItem();
        miClose.SetBinding(MenuItem.HeaderProperty, Loc.Bind("Ctx.CloseTab"));
        miClose.Click += (_, _) => CloseTab(ti);
        cm.Items.Add(miDetach);
        cm.Items.Add(miMerge);
        cm.Items.Add(miClose);
        panel.ContextMenu = cm;

        return (panel, text, dot);
    }

    /// <summary>연결 상태 점 색: 연결됨=초록, AI 양보=보라, 재연결 대기=호박, 끊김=회색.</summary>
    private static Brush DotFor(UartDocumentView doc) =>
        doc.IsConnected ? DotConnected
        : doc.IsPortReleased ? DotReleased
        : doc.IsReconnecting ? DotReconnecting
        : DotIdle;

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

        if (!IsSplit)
        {
            var doc = ActiveDoc ?? docs[0];
            DetachViewFromParent(doc);
            ContentHost.Children.Add(doc);
            return;
        }

        var (rows, cols) = ComputeLayout(docs.Count);
        var grid = new Grid();
        const double SplitterSize = 6;

        // 콘텐츠 트랙(Star) 사이에 스플리터 트랙(고정 6px)을 끼워 넣는다: [콘텐츠, 스플리터, 콘텐츠, …]
        // → 콘텐츠는 짝수 인덱스, 스플리터는 홀수 인덱스 트랙에 위치.
        for (int c = 0; c < cols; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (c < cols - 1) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterSize) });
        }
        for (int r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (r < rows - 1) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterSize) });
        }

        int totalCols = grid.ColumnDefinitions.Count;
        int totalRows = grid.RowDefinitions.Count;

        for (int i = 0; i < docs.Count; i++)
        {
            var panel = BuildPanel(docs[i]);
            Grid.SetRow(panel, (i / cols) * 2);
            Grid.SetColumn(panel, (i % cols) * 2);
            grid.Children.Add(panel);
        }

        // 세로 스플리터(열 경계) — 좌우 크기 조절
        for (int c = 1; c < totalCols; c += 2)
        {
            var gs = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = SplitterBrush,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Columns,
                ShowsPreview = false,
            };
            Grid.SetColumn(gs, c);
            Grid.SetRow(gs, 0);
            Grid.SetRowSpan(gs, totalRows);
            grid.Children.Add(gs);
        }
        // 가로 스플리터(행 경계) — 상하 크기 조절
        for (int r = 1; r < totalRows; r += 2)
        {
            var gs = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = SplitterBrush,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Rows,
                ShowsPreview = false,
            };
            Grid.SetRow(gs, r);
            Grid.SetColumn(gs, 0);
            Grid.SetColumnSpan(gs, totalCols);
            grid.Children.Add(gs);
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
            // 두께는 활성/비활성 무관하게 고정 — 활성 전환 시 내부 폭이 변해 터미널이 reflow(글자 이동)되는 것 방지.
            // 하이라이트는 색상으로만 표현한다.
            BorderThickness = new Thickness(2),
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
        // PreviewMouseDown 은 터널링이라 클릭 대상(ComboBoxItem 등)보다 먼저 실행된다.
        // 여기서 무조건 터미널로 포커스를 옮기면 열려 있던 드롭다운이 닫혀 항목 선택이 취소됐다
        // (CMD 그룹을 바꿀 수 없던 원인 — 진단 로그로 확인: 항목을 누른 시점에 이미 열림=False,
        //  MouseUp 은 팝업이 사라진 자리 아래의 입력창이 받았다).
        border.PreviewMouseDown += (_, ev) =>
            ActivatePanel(doc, focusTerminal: !IsInteractiveSource(ev.OriginalSource));

        _panelBorders[doc] = border;
        _panelTitleTexts[doc] = title;
        _panelDots[doc] = dot;
        return border;
    }

    private (int rows, int cols) ComputeLayout(int n)
    {
        if (n <= 1) return (1, 1);
        switch (_split)
        {
            case SplitLayout.Columns: return (1, n); // 모두 좌우로(열)
            case SplitLayout.Rows: return (n, 1);    // 모두 상하로(행)
            default:                                  // Grid: 균형 격자(ceil√n)
                int cols = (int)Math.Ceiling(Math.Sqrt(n));
                int rows = (int)Math.Ceiling((double)n / cols);
                return (rows, cols);
        }
    }

    /// <summary>
    /// 분할 패널을 활성화한다. <paramref name="focusTerminal"/> 이 false 면 포커스를 건드리지 않는다 —
    /// 콤보 드롭다운·입력창처럼 <b>스스로 포커스를 가져야 하는 컨트롤</b>을 클릭한 경우다.
    /// </summary>
    private void ActivatePanel(UartDocumentView doc, bool focusTerminal = true)
    {
        var ti = TabOf(doc);
        if (ti is not null) Tabs.SelectedItem = ti; // 탭 동기화(SelectionChanged 에서 하이라이트/포커스)
        UpdateSplitHighlights();
        if (focusTerminal) doc.FocusTerminal();
    }

    /// <summary>
    /// 클릭 대상이 자기 포커스를 필요로 하는 컨트롤인지(그 안이면 터미널로 포커스를 빼앗지 않는다).
    /// <b>논리</b> 트리를 올라간다 — 콤보 드롭다운 항목은 자체 팝업 창에 떠서 visual 조상이 끊기지만,
    /// 논리 부모를 따라가면 ComboBox 까지 도달한다.
    /// </summary>
    private static bool IsInteractiveSource(object? source)
    {
        var d = source as DependencyObject;
        for (int hop = 0; d is not null && hop < 40; hop++)
        {
            if (d is ComboBox or System.Windows.Controls.Primitives.TextBoxBase
                  or System.Windows.Controls.Primitives.ButtonBase or MenuItem)
                return true;
            d = LogicalTreeHelper.GetParent(d)
                ?? (d is FrameworkElement fe ? fe.TemplatedParent ?? VisualTreeHelper.GetParent(d) : null);
        }
        return false;
    }

    private void UpdateSplitHighlights()
    {
        foreach (var (doc, border) in _panelBorders)
        {
            bool active = ReferenceEquals(ActiveDoc, doc);
            border.BorderBrush = active ? AccentBrush : PanelBorderInactive; // 색상만 변경(두께 고정 — reflow 방지)
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
            StatusText.Text = Loc.S("Shell.OnlyOneTab");
            return;
        }
        var floatWin = new ShellWindow(_state, _commands, _sessions, isPrimary: false);
        floatWin.Show();
        MoveTab(ti, floatWin);
        floatWin.Activate();
    }

    private void MergeTab(TabItem ti)
    {
        if (_isPrimary || Primary is null)
        {
            StatusText.Text = Loc.S("Shell.AlreadyMain");
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
        if (IsSplit) UpdateSplitHighlights();
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
        SyncSplitChrome();
        MenuAutoReconnect.IsChecked = _state.AutoReconnect;
        MenuCommandBar.IsChecked = _state.ShowCommandBar;
        // 탭별 값이므로 활성 탭을 따른다(탭이 없으면 마지막으로 쓴 기본값).
        MenuResetOnOpen.IsChecked = doc?.ResetOnOpen ?? _state.ResetOnOpen;
        SyncNewlineChrome();
        SyncThemeChrome();
        RefreshMcpChrome();
    }

    private void RefreshMcpChrome()
    {
        var doc = ActiveDoc;
        MenuMcpEnabled.IsChecked = doc?.McpEnabled ?? false;
        MenuMcpReadOnly.IsChecked = doc?.McpReadOnly ?? false;

        // 색은 테마에서 읽는다. 예전에는 여기서 new SolidColorBrush(0xD7BA7D) 처럼 코드에 박아
        // 라이트 테마에서 1.8:1 이 되어 '읽기 전용' 안전 표시가 상태바에서 사라졌다.
        if (doc is null || !doc.McpEnabled)
        {
            McpStatusText.Text = Loc.S("Status.McpOff");
            McpStatusText.Foreground = Theme.Brush("TextFaint");
        }
        else
        {
            string pipe = McpPipeServer.PipeNameFor(doc.PortName);
            McpStatusText.Text = Loc.F(doc.McpReadOnly ? "Status.McpOnReadOnly" : "Status.McpOn", pipe);
            McpStatusText.Foreground = Theme.Brush(doc.McpReadOnly ? "Amber" : "Green");
        }
    }

    // ── 전역 단축키 ──────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 아래 단축키는 모두 1회성 명령이다. 키 자동반복(약 30회/초)을 그대로 받으면
        // 탭이 여러 개 열리거나 명령 바가 깜빡이며 state.json 이 반복 재기록된다.
        // (type-through 의 화살표 반복 등은 이 창 핸들러가 아니라 문서 핸들러가 처리하므로 영향 없음.)
        if (e.IsRepeat) return;

        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Alt) != 0 && e.SystemKey == Key.N)
        { ActiveDoc?.ReconnectViaDialog(); e.Handled = true; return; }
        if ((mods & ModifierKeys.Alt) != 0 && e.SystemKey == Key.I)
        { ActiveDoc?.Disconnect(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.T)
        { NewTab(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.W)
        { CloseActiveTab(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.F)
        { ActiveDoc?.ShowFind(); e.Handled = true; return; }
        // Alt+B: 명령 바 토글. Alt 계열이라 터미널 type-through(단독 Ctrl+문자 = 제어 바이트)를 잠식하지 않는다.
        if ((mods & ModifierKeys.Alt) != 0 && e.SystemKey == Key.B)
        { ToggleCommandBar(); e.Handled = true; return; }
        // Alt+R = 하드웨어 리셋, Alt+Shift+R = 부트로더 진입(ESP32 DTR/RTS 자동 리셋 회로).
        if ((mods & ModifierKeys.Alt) != 0 && e.SystemKey == Key.R)
        {
            if ((mods & ModifierKeys.Shift) != 0) BoardBootloader();
            else BoardHardReset();
            e.Handled = true;
            return;
        }
    }

    /// <summary>Ctrl+마우스휠 → 활성 창(패널)의 폰트 크기 조절(스크롤 대신). Ctrl 없으면 뷰가 스크롤 처리.</summary>
    private void OnPreviewMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0) return;
        ActiveDoc?.AdjustFont(e.Delta > 0 ? +1 : -1);
        e.Handled = true; // TerminalView 의 휠 스크롤로 전달되지 않게
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
    // ── 보드 제어(ESP32 DTR/RTS 자동 리셋 회로) ─────────────────────────────────
    // 시퀀스는 100ms+50ms 대기가 있어 비동기다. 결과는 문서가 상태바로 보고하므로 여기서는 기다리지 않는다.

    private void HardReset_Click(object sender, RoutedEventArgs e) => BoardHardReset();
    private void Bootloader_Click(object sender, RoutedEventArgs e) => BoardBootloader();

    private void BoardHardReset() => _ = ActiveDoc?.HardResetAsync();
    private void BoardBootloader() => _ = ActiveDoc?.EnterBootloaderAsync();

    private void Flash_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ShowFlashDialog();

    /// <summary>
    /// '열 때 보드 리셋'은 속도처럼 <b>탭(연결)별</b> 값이다 — 보드마다 다르고 세션에 함께 저장된다.
    /// 그래서 전역 동기화(자동 재연결/타임스탬프 방식)를 하지 않고 활성 탭에만 적용한다.
    /// </summary>
    private void ResetOnOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveDoc is not { } doc)
        {
            MenuResetOnOpen.IsChecked = _state.ResetOnOpen; // 탭이 없으면 되돌린다(허상 체크 방지)
            return;
        }
        doc.SetResetOnOpen(MenuResetOnOpen.IsChecked);
        RefreshChrome();
    }

    // ── 개행(New-line) 규약 ──────────────────────────────────────────────────────

    // 개행도 '열 때 보드 리셋'과 같은 탭별 접속 속성(장치마다 다르다) → 활성 탭에만 적용하고
    // 세션에 저장된다. _state 의 값은 새 탭/세션 없이 열 때의 기본값 역할만 한다.

    private void NewlineRx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<ReceiveNewline>(tag, out var mode))
            return;
        if (ActiveDoc is { } doc) doc.SetReceiveNewline(mode);
        else { _state.NewlineRx = mode; _state.Save(); } // 탭이 없으면 기본값만 바꾼다
        SyncNewlineChrome();
    }

    private void NewlineTx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<TransmitNewline>(tag, out var mode))
            return;
        if (ActiveDoc is { } doc) doc.SetTransmitNewline(mode);
        else { _state.NewlineTx = mode; _state.Save(); }
        SyncNewlineChrome();
    }

    /// <summary>개행 메뉴의 체크를 활성 탭 값과 동기화(라디오 동작). 탭이 없으면 기본값을 보여준다.</summary>
    private void SyncNewlineChrome()
    {
        var doc = ActiveDoc;
        var rx = doc?.NewlineRx ?? _state.NewlineRx;
        var tx = doc?.NewlineTx ?? _state.NewlineTx;
        MenuRxCrLf.IsChecked = rx == ReceiveNewline.CrLf;
        MenuRxLf.IsChecked = rx == ReceiveNewline.Lf;
        MenuRxCr.IsChecked = rx == ReceiveNewline.Cr;
        MenuRxAuto.IsChecked = rx == ReceiveNewline.Auto;
        MenuTxCr.IsChecked = tx == TransmitNewline.Cr;
        MenuTxCrLf.IsChecked = tx == TransmitNewline.CrLf;
        MenuTxLf.IsChecked = tx == TransmitNewline.Lf;
    }

    private void SaveSession_Click(object sender, RoutedEventArgs e) => ActiveDoc?.SaveCurrentAsSession();

    private void ManageSessions_Click(object sender, RoutedEventArgs e)
        => SessionManagerDialog.ShowManager(_sessions, _commands, this);
    private void Detach_Click(object sender, RoutedEventArgs e) { if (Tabs.SelectedItem is TabItem ti) DetachTab(ti); }
    private void Merge_Click(object sender, RoutedEventArgs e) { if (Tabs.SelectedItem is TabItem ti) MergeTab(ti); }
    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseActiveTab();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Copy();
    private void Paste_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Paste();
    private void ClearScreen_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ClearScreen();
    private void ClearBuffer_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ClearBuffer();
    private void ScrollEnd_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ScrollEnd();
    private void SaveLog_Click(object sender, RoutedEventArgs e) => ActiveDoc?.SaveVisibleLog();
    private void Find_Click(object sender, RoutedEventArgs e) => ActiveDoc?.ShowFind();

    private void DiagCapture_Click(object sender, RoutedEventArgs e)
    {
        bool on = MenuDiagCapture.IsChecked;
        DiagLog.Capture = on;
        _state.DiagCapture = on;
        _state.Save();
        DiagLog.Info($"진단 캡처 {(on ? "켜짐" : "꺼짐")}");
        // 전역 설정 — 열린 모든 창의 메뉴 체크 동기화(AutoReconnect 와 같은 방침).
        foreach (var w in Application.Current.Windows.OfType<ShellWindow>())
            w.MenuDiagCapture.IsChecked = on;
    }
    private void FontLarger_Click(object sender, RoutedEventArgs e) => ActiveDoc?.AdjustFont(+1);
    private void FontSmaller_Click(object sender, RoutedEventArgs e) => ActiveDoc?.AdjustFont(-1);

    /// <summary>테마 전환 — 전역 설정이라 열린 모든 창의 메뉴 체크를 함께 맞춘다(적용 자체는 Theme.Apply 가 전역).</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<AppTheme>(tag, out var theme))
            return;

        Theme.Apply(theme);
        _state.Theme = theme;
        _state.Save();

        foreach (var w in Application.Current.Windows.OfType<ShellWindow>())
            w.SyncThemeChrome();
    }

    private void SyncThemeChrome()
    {
        MenuThemeDark.IsChecked = Theme.Current == AppTheme.Dark;
        MenuThemeLight.IsChecked = Theme.Current == AppTheme.Light;
        MenuLangKo.IsChecked = Loc.Language == AppLanguage.Korean;
        MenuLangEn.IsChecked = Loc.Language == AppLanguage.English;
    }

    /// <summary>
    /// 언어 전환. 재시작하면 시리얼 연결과 MCP 서버가 끊기므로, 문자열을 인덱서 바인딩으로 두고
    /// 알림 한 번으로 화면을 갈아끼운다(연결 유지). 코드에서 만든 문자열은 Loc.Changed 를 받는 쪽이 다시 만든다.
    /// </summary>
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<AppLanguage>(tag, out var lang))
            return;

        Loc.SetLanguage(lang);   // 각 창이 Loc.Changed 를 구독해 스스로 갱신한다
        _state.Language = lang;
        _state.Save();

        foreach (var w in Application.Current.Windows.OfType<ShellWindow>())
            w.SyncThemeChrome();  // 메뉴 체크 표시(라디오)는 상태 동기화라 별도
    }

    /// <summary>
    /// 언어 전환 알림. XAML 바인딩은 스스로 갱신되지만 <b>코드가 조립해 보관한</b> 문자열
    /// (탭 제목·상태바·메트릭·칩 툴팁)은 다시 만들어야 한다.
    /// 이 구독이 빠져 있어서 <c>Loc.Changed</c> 는 발생만 하고 받는 곳이 하나도 없었다.
    /// </summary>
    private void OnLanguageChanged()
    {
        foreach (var d in AllDocs()) d.RefreshLocalizedText();
        RefreshChrome();
    }

    private void Timestamps_Click(object sender, RoutedEventArgs e)
    {
        bool on = MenuTimestamps.IsChecked;
        _state.ShowTimestamps = on;
        _state.Save();
        // 전역 설정 — 모든 창/탭과 메뉴 체크 동기화(명령 바/자동 재연결과 같은 방침).
        foreach (var w in Application.Current.Windows.OfType<ShellWindow>())
        {
            w.MenuTimestamps.IsChecked = on;
            foreach (var d in w.AllDocs()) d.SetTimestamps(on);
        }
    }

    /// <summary>툴바 아이콘/메뉴의 IsChecked 를 현재 레이아웃과 동기화(라디오 동작).</summary>
    private void SyncSplitChrome()
    {
        BtnSplitSingle.IsChecked = _split == SplitLayout.Single;
        BtnSplitV.IsChecked = _split == SplitLayout.Columns;
        BtnSplitH.IsChecked = _split == SplitLayout.Rows;
        BtnSplitGrid.IsChecked = _split == SplitLayout.Grid;
        MenuSplitSingle.IsChecked = _split == SplitLayout.Single;
        MenuSplitV.IsChecked = _split == SplitLayout.Columns;
        MenuSplitH.IsChecked = _split == SplitLayout.Rows;
        MenuSplitGrid.IsChecked = _split == SplitLayout.Grid;
    }

    private void SetSplitLayout(SplitLayout layout)
    {
        _split = layout;
        RenderContent();
        RefreshChrome();
        ActiveDoc?.FocusTerminal();
    }

    private void SplitSingle_Click(object sender, RoutedEventArgs e) => SetSplitLayout(SplitLayout.Single);
    private void SplitV_Click(object sender, RoutedEventArgs e) => SetSplitLayout(SplitLayout.Columns);
    private void SplitH_Click(object sender, RoutedEventArgs e) => SetSplitLayout(SplitLayout.Rows);
    private void SplitGrid_Click(object sender, RoutedEventArgs e) => SetSplitLayout(SplitLayout.Grid);

    // ── 저장 명령 ────────────────────────────────────────────────────────────────

    private void CommandBar_Click(object sender, RoutedEventArgs e) => SetCommandBar(MenuCommandBar.IsChecked);

    private void ToggleCommandBar() => SetCommandBar(!_state.ShowCommandBar);

    /// <summary>칩 바 표시 여부는 전역 설정 — 열린 모든 창/탭과 메뉴 체크를 함께 동기화한다(AutoReconnect 와 같은 방침).</summary>
    private void SetCommandBar(bool show)
    {
        _state.ShowCommandBar = show;
        _state.Save();
        foreach (var w in Application.Current.Windows.OfType<ShellWindow>())
        {
            w.MenuCommandBar.IsChecked = show;
            foreach (var d in w.AllDocs()) d.SetCommandBarVisible(show);
        }
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e) => ActiveDoc?.SaveCurrentInputAsCommand();

    private void EditCommands_Click(object sender, RoutedEventArgs e)
        => CommandEditDialog.ShowEditor(_commands, this);

    private void McpEnabled_Click(object sender, RoutedEventArgs e)
    { ActiveDoc?.McpSetEnabled(MenuMcpEnabled.IsChecked); RefreshMcpChrome(); }
    private void McpReadOnly_Click(object sender, RoutedEventArgs e)
    { ActiveDoc?.McpSetReadOnly(MenuMcpReadOnly.IsChecked); RefreshMcpChrome(); }
    private void McpCopyCmd_Click(object sender, RoutedEventArgs e) => ActiveDoc?.McpCopyCommand();

    private void About_Click(object sender, RoutedEventArgs e)
        => new AboutDialog { Owner = this }.ShowDialog();

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
        if (IsSplit) RenderContent();
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
        Loc.Changed -= OnLanguageChanged;   // 생성자의 += 와 짝(정적 이벤트 → 창을 붙잡는다)

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
