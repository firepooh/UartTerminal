using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Rendering;

/// <summary>스크롤바 갱신용 메트릭.</summary>
public readonly record struct ScrollMetrics(int TotalLines, int TopLine, int ViewportRows, bool FollowTail);

/// <summary>
/// 커스텀 터미널 렌더러(README §4.1/§4.3). 논리 라인 버퍼를 현재 폭으로 soft-wrap 하고,
/// 뷰포트에 보이는 행만 GlyphRun 으로 그린다(가상화). AdvanceWidths 를 셀폭×(1|2)로 강제해
/// 전각/폴백 폰트에서도 열 정렬을 보장한다. 리사이즈 시 reflow 가 "렌더 폭 변경"만으로 해결된다.
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    private sealed class WrapEntry
    {
        public int Cols;
        public int Version;
        public int EffLen = -1; // 후행 공백을 제외한 유효 길이(커서 포함). 커서 이동으로 바뀔 수 있어 캐시 키에 포함.
        public int[] Starts = { 0 };
    }

    private readonly struct VisRow
    {
        public readonly long AbsLine;
        public readonly int StartCell;    // 논리 라인 내 이 행의 첫 셀 인덱스
        public readonly int StartColumn;  // 논리 라인 내 이 행의 시작 열(전각 폭 반영)
        public readonly LineType Type;
        public readonly Cell[] Cells;     // [StartCell, StartCell+Cells.Length)
        public readonly DateTime? Stamp;  // 논리 라인의 수신 시각(첫 시각 행에서만 그림)
        public VisRow(long absLine, int startCell, int startColumn, LineType type, Cell[] cells, DateTime? stamp)
        {
            AbsLine = absLine; StartCell = startCell; StartColumn = startColumn; Type = type; Cells = cells; Stamp = stamp;
        }
    }

    /// <summary>단일 버퍼 락 구간에서 캡처한 렌더 스냅샷(보이는 행 + 개정/총라인/커서). TOCTOU·커서 불일치 방지.</summary>
    private readonly struct Snapshot
    {
        public readonly VisRow[] Rows;
        public readonly int TopListIndex;
        public readonly long Revision;
        public readonly int TotalLines;
        public readonly long CursorAbs;
        public readonly int CursorCol;
        public Snapshot(VisRow[] rows, int topListIndex, long revision, int totalLines, long cursorAbs, int cursorCol)
        {
            Rows = rows; TopListIndex = topListIndex; Revision = revision;
            TotalLines = totalLines; CursorAbs = cursorAbs; CursorCol = cursorCol;
        }
    }

    private static readonly string[] PrimaryFonts = { "Cascadia Code", "Cascadia Mono", "D2Coding", "Consolas", "Courier New" };
    private static readonly string[] FallbackFonts =
        { "Malgun Gothic", "맑은 고딕", "Gulim", "굴림" };   // loc:data — 폰트 패밀리 이름

    private readonly TerminalBuffer _buffer;
    private readonly TerminalPalette _palette = TerminalPalette.Current;
    private readonly ConditionalWeakTable<LogicalLine, WrapEntry> _wrapCache = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly DispatcherTimer _timer;

    private FontMetrics? _metrics;
    private double _fontSize = 14.0;
    private double _metricsBuiltDpi = -1;
    private double _metricsBuiltFontSize = -1;

    private long _lastRevision = -1;
    private bool _forceRender = true;

    // 스크롤 상태: 앵커는 절대 라인 번호(트림에도 안정). 팔로우 중이면 항상 바닥 표시.
    private bool _followTail = true;
    private long _topAbsLine;
    private int _topSubRow;

    // 마지막 렌더의 보이는 행(히트 테스트/선택용)
    private VisRow[] _visible = Array.Empty<VisRow>();
    private int _columns;
    private int _rows;

    // 선택: (절대 라인, 셀 인덱스)
    private bool _hasSelection;
    private bool _selecting;
    private (long Line, int Cell) _selAnchor;
    private (long Line, int Cell) _selFocus;

    // 드래그 선택이 뷰 밖으로 나갈 때의 자동 스크롤
    private DispatcherTimer? _autoScrollTimer;
    private int _autoScrollDir;    // -1=위, +1=아래, 0=정지
    private Point _lastDragPoint;  // 최근 드래그 좌표(자동 스크롤 시 열 위치 유지용)

    // 타임스탬프 gutter(수신 시각 표시). 켜지면 왼쪽에 고정폭 gutter 를 확보하고 콘텐츠를 그만큼 오른쪽으로 민다
    // (콘텐츠는 TranslateTransform 으로 이동 → 선택/커서/wrap 좌표계는 그대로, HitTest 만 gutter 보정).
    private bool _showTimestamps;
    private double _tsGutter;                 // 현재 프레임의 gutter 픽셀 폭(0=꺼짐)
    private const int TsGutterChars = 13;     // "HH:mm:ss.fff " 폭(문자)
    private Typeface? _tsTypeface;
    // 색은 테마에서. 원래 #6E7A8A 는 배경 대비 4.3:1 로 작은 글씨에 아슬아슬했다.
    private static Color TsColor => Theme.ColorOr("C.TermTimestamp", Color.FromRgb(0x8B, 0x97, 0xA8));

    public bool ShowTimestamps
    {
        get => _showTimestamps;
        set { if (_showTimestamps == value) return; _showTimestamps = value; _forceRender = true; }
    }

    // 검색 하이라이트: 절대 라인 → 매치 구간 목록(셀 시작, 길이). 현재 매치는 별도로 강조.
    public readonly record struct SearchHit(long AbsLine, int StartCell, int Length);
    private readonly Dictionary<long, List<(int start, int len)>> _searchLines = new();
    private SearchHit? _currentHit;
    private static Color SearchMatchBg => Theme.ColorOr("C.TermSearchMatch", Color.FromRgb(0x4B, 0x46, 0x12));
    private static Color SearchCurrentBg => Theme.ColorOr("C.TermSearchCurrent", Color.FromRgb(0x8A, 0x66, 0x00));

    /// <summary>검색 매치 집합과 현재 매치 인덱스를 설정(하이라이트 갱신).</summary>
    public void SetSearch(IReadOnlyList<SearchHit> hits, int current)
    {
        _searchLines.Clear();
        foreach (var h in hits)
        {
            if (!_searchLines.TryGetValue(h.AbsLine, out var list)) { list = new(); _searchLines[h.AbsLine] = list; }
            list.Add((h.StartCell, h.Length));
        }
        _currentHit = (current >= 0 && current < hits.Count) ? hits[current] : null;
        _forceRender = true;
    }

    public void ClearSearch()
    {
        if (_searchLines.Count == 0 && _currentHit is null) return;
        _searchLines.Clear();
        _currentHit = null;
        _forceRender = true;
    }

    /// <summary>지정한 절대 라인을 화면 상단 근처로 스크롤(검색 매치 이동용).</summary>
    public void ScrollLineIntoView(long absLine)
    {
        lock (_buffer.SyncRoot)
        {
            _followTail = false;
            long lo = _buffer.TrimmedCount;
            long hi = _buffer.TrimmedCount + Math.Max(0, _buffer.LineCount - 1);
            _topAbsLine = Math.Clamp(absLine, lo, hi);
            _topSubRow = 0;
        }
        _forceRender = true;
    }

    /// <summary>이 셀이 검색 매치/현재 매치에 속하는지(배경 하이라이트용).</summary>
    private (bool match, bool current) SearchState(long absLine, int cell)
    {
        bool current = _currentHit is { } ch && ch.AbsLine == absLine
                       && cell >= ch.StartCell && cell < ch.StartCell + ch.Length;
        if (current) return (true, true);
        if (_searchLines.Count > 0 && _searchLines.TryGetValue(absLine, out var ranges))
            foreach (var (st, ln) in ranges)
                if (cell >= st && cell < st + ln) return (true, false);
        return (false, false);
    }

    public event Action<ScrollMetrics>? ScrollMetricsChanged;

    /// <summary>드래그 선택 완료 시 선택 텍스트를 전달(TeraTerm식 자동 복사).</summary>
    public event Action<string>? AutoCopyRequested;

    /// <summary>우클릭 시 붙여넣기 요청(TeraTerm식).</summary>
    public event Action? PasteRequested;

    public TerminalView(TerminalBuffer buffer)
    {
        _buffer = buffer;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60Hz 배칭
        };
        _timer.Tick += (_, _) =>
        {
            if (_forceRender || _buffer.Revision != _lastRevision)
                InvalidateVisual();
        };
        _timer.Start();

    }

    /// <summary>
    /// 탭/문서가 닫힐 때 호출(소유자: <c>UartDocumentView.CloseDocument</c>).
    /// 렌더 타이머와 정적 이벤트 구독을 끊는다 — 실행 중인 <see cref="DispatcherTimer"/> 는
    /// Dispatcher 가 강하게 참조하므로, 멈추지 않으면 이 뷰와 버퍼·문서 그래프가 영구히 살아남고
    /// 닫힌 탭이 계속 60Hz 로 스냅샷을 만든다.
    /// </summary>
    public void Shutdown()
    {
        _timer.Stop();
        _autoScrollTimer?.Stop();
        ScrollMetricsChanged = null;
        AutoCopyRequested = null;
        PasteRequested = null;
        _brushes.Clear();
        _searchLines.Clear();
        _visible = Array.Empty<VisRow>();
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (value < 6) value = 6;
            if (Math.Abs(value - _fontSize) < 0.01) return;
            _fontSize = value;
            _metrics = null;
            _forceRender = true;
        }
    }

    public int Columns => _columns;
    public int Rows => _rows;

    public void ScrollToEnd()
    {
        _followTail = true;
        ClearSelection();
        _forceRender = true;
    }

    public void SetTopLine(int listIndex)
    {
        lock (_buffer.SyncRoot)
        {
            _followTail = false;
            _topAbsLine = _buffer.TrimmedCount + Math.Clamp(listIndex, 0, Math.Max(0, _buffer.LineCount - 1));
            _topSubRow = 0;
        }
        _forceRender = true;
    }

    public void ScrollByRows(int deltaRows)
    {
        lock (_buffer.SyncRoot)
        {
            EnsureAnchorMaterialized();
            if (deltaRows < 0) MoveAnchorUp(-deltaRows);
            else MoveAnchorDown(deltaRows);
        }
        _forceRender = true;

        // 선택 중 스크롤(휠/PageUp·Down/자동 스크롤 공통) → 스크롤 방향의 가장자리로 선택을 확장.
        if (_selecting && deltaRows != 0)
        {
            double edgeY = deltaRows < 0 ? 0 : Math.Max(0, ActualHeight - 1);
            UpdateSelectionFocusFromPoint(new Point(_lastDragPoint.X, edgeY));
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _forceRender = true; // 폭 변경 → reflow
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int rows = e.Delta / 120 * 3;
        ScrollByRows(-rows);
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var metrics = EnsureMetrics();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(GetBrush(_palette.DefaultBackground), null, new Rect(0, 0, w, h));
        if (metrics is null || w <= 0 || h <= 0)
            return;

        _tsGutter = _showTimestamps ? TsGutterChars * metrics.CellWidth : 0;
        _columns = Math.Max(1, (int)((w - _tsGutter) / metrics.CellWidth));
        _rows = Math.Max(1, (int)(h / metrics.CellHeight));

        try
        {
            var snap = BuildSnapshot(_columns, _rows);
            _visible = snap.Rows;

            // 선택 정규화
            (long Line, int Cell) selMin = default, selMax = default;
            if (_hasSelection)
            {
                (selMin, selMax) = NormalizeSelection();
            }

            // 타임스탬프는 변환 밖(gutter)에 그린다 — 콘텐츠 좌표계를 건드리지 않는다.
            if (_tsGutter > 0) DrawTimestamps(dc, metrics);

            bool pushed = false;
            if (_tsGutter > 0) { dc.PushTransform(new TranslateTransform(_tsGutter, 0)); pushed = true; }
            try
            {
                for (int r = 0; r < _visible.Length; r++)
                {
                    double y = r * metrics.CellHeight;
                    RenderRow(dc, metrics, _visible[r], y, selMin, selMax);
                }
                if (_followTail)
                    DrawCursor(dc, metrics, snap.CursorAbs, snap.CursorCol);
            }
            finally { if (pushed) dc.Pop(); } // push/pop 균형 보장(DrawingContext.Close 검증 통과)

            // 개정 번호는 스냅샷과 동일 락 구간에서 캡처됨 → 락 밖 재읽기로 인한 stale(누락) 방지
            _lastRevision = snap.Revision;
            _forceRender = false;

            ScrollMetricsChanged?.Invoke(new ScrollMetrics(snap.TotalLines, snap.TopListIndex, _rows, _followTail));
        }
        catch (Exception ex)
        {
            // 렌더 파이프라인 예외가 앱을 종료시키지 않도록 방어.
            // 재시도 상태를 반드시 여기서 정리한다 — 성공 경로의 `_forceRender = false` 를
            // 건너뛰면 16ms 타이머가 같은 예외를 초당 60회 재현하고, 매번 동기 파일 로그를
            // UI 스레드에서 써서 앱이 멈추고 diag.log 롤링으로 원인(첫 예외)까지 지워졌다.
            _forceRender = false;
            _lastRevision = _buffer.Revision;

            // 같은 예외의 반복은 한 번만 기록한다(증거 보존).
            string sig = $"{ex.GetType().Name}:{ex.Message}";
            if (sig != _lastRenderErrorSig)
            {
                _lastRenderErrorSig = sig;
                UartTerminal.DiagLog.Exception("OnRender", ex);
            }
        }
    }

    /// <summary>직전 렌더 예외의 서명. 같은 예외를 매 프레임 기록해 진단 로그를 덮어쓰지 않기 위한 것.</summary>
    private string? _lastRenderErrorSig;

    private void RenderRow(DrawingContext dc, FontMetrics m, in VisRow row, double y,
                           (long Line, int Cell) selMin, (long Line, int Cell) selMax)
    {
        var cells = row.Cells;

        // 1) 배경 패스(명시적 배경 / reverse / 선택)
        // 폭은 원시값(CharWidth.Width) 사용 — wrap/레이아웃/커서와 동일 기준(제로폭=0은 열 미전진).
        double x = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            var attr = cells[i].Attributes;
            int cw = CharWidth.Width(cells[i].Ch);
            double cellPx = cw * m.CellWidth;

            if (cellPx > 0)
            {
                int ci = row.StartCell + i;
                bool selected = _hasSelection && InSelection(row.AbsLine, ci, selMin, selMax);
                var (isMatch, isCurrent) = SearchState(row.AbsLine, ci);
                if (selected)
                {
                    dc.DrawRectangle(GetBrush(_palette.SelectionBackground), null,
                        new Rect(x, y, cellPx, m.CellHeight));
                }
                else if (isMatch)
                {
                    dc.DrawRectangle(GetBrush(isCurrent ? SearchCurrentBg : SearchMatchBg), null,
                        new Rect(x, y, cellPx, m.CellHeight));
                }
                else if (_palette.HasExplicitBackground(attr))
                {
                    var bg = attr.Flags.HasFlag(CellFlags.Reverse)
                        ? _palette.ResolveForeground(attr)
                        : _palette.ResolveBackground(attr);
                    dc.DrawRectangle(GetBrush(bg), null, new Rect(x, y, cellPx, m.CellHeight));
                }
            }
            x += cellPx;
        }

        // 2) 글리프 패스: 같은 전경색(typeface 포함)의 연속 셀을 하나의 GlyphRun 으로
        x = 0;
        var glyphs = new List<ushort>();
        var advances = new List<double>();
        GlyphTypeface? runTypeface = null;
        Color runColor = default;
        double runX = 0;

        void Flush()
        {
            if (glyphs.Count == 0 || runTypeface is null) { glyphs.Clear(); advances.Clear(); runTypeface = null; return; }
            var run = new GlyphRun(
                runTypeface, 0, false, m.FontSize, m.PixelsPerDip,
                glyphs.ToArray(), new Point(runX, y + m.BaselineY),
                advances.ToArray(), null, null, null, null, null, null);
            dc.DrawGlyphRun(GetBrush(runColor), run);
            glyphs.Clear();
            advances.Clear();
            runTypeface = null;
        }

        for (int i = 0; i < cells.Length; i++)
        {
            char ch = cells[i].Ch;
            var attr = cells[i].Attributes;
            int cw = CharWidth.Width(ch); // 원시 폭(제로폭=0 → advance 0으로 이전 글리프에 겹쳐 그림)
            double cellPx = cw * m.CellWidth;

            Color fg = attr.Flags.HasFlag(CellFlags.Reverse)
                ? _palette.ResolveBackground(attr)
                : _palette.ResolveForeground(attr);

            bool drawable = ch != ' ' && !char.IsControl(ch);
            if (drawable && m.TryGetGlyph(ch, out var tf, out ushort gi))
            {
                // typeface 또는 색이 바뀌면 run flush
                if (runTypeface is not null && (!ReferenceEquals(runTypeface, tf) || runColor != fg))
                    Flush();
                if (runTypeface is null)
                {
                    runTypeface = tf;
                    runColor = fg;
                    runX = x;
                }
                glyphs.Add(gi);
                advances.Add(cellPx);
            }
            else
            {
                // 공백/미지원: run 을 끊고 자리만 건너뜀
                Flush();
            }
            x += cellPx;
        }
        Flush();
    }

    private void DrawCursor(DrawingContext dc, FontMetrics m, long curAbs, int curCol)
    {
        // 커서 위치(curAbs/curCol)는 BuildSnapshot 과 동일 락 구간에서 캡처됨 → 본문과 일관

        // 현재 라인의 커서가 있는 시각 행을 찾는다
        for (int r = 0; r < _visible.Length; r++)
        {
            var vr = _visible[r];
            if (vr.AbsLine != curAbs) continue;
            int rowStartCol = vr.StartColumn;
            int rowEndCol = rowStartCol + RowWidth(vr);
            if (curCol >= rowStartCol && curCol <= rowEndCol)
            {
                double x = (curCol - rowStartCol) * m.CellWidth;
                double y = r * m.CellHeight;
                dc.DrawRectangle(GetBrush(_palette.CursorColor), null,
                    new Rect(x, y, Math.Max(1, m.CellWidth * 0.15), m.CellHeight));
                return;
            }
        }
    }

    /// <summary>각 논리 라인의 첫 시각 행 앞 gutter 에 수신 시각(HH:mm:ss.fff)을 흐린 색으로 그린다.</summary>
    private void DrawTimestamps(DrawingContext dc, FontMetrics m)
    {
        _tsTypeface ??= new Typeface(new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var brush = GetBrush(TsColor);
        for (int r = 0; r < _visible.Length; r++)
        {
            var row = _visible[r];
            if (row.StartCell != 0 || row.Stamp is not { } ts) continue; // 래핑 연속 행/빈 라인은 건너뜀
            var ft = new FormattedText(
                ts.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tsTypeface,
                m.FontSize, brush, m.PixelsPerDip);
            dc.DrawText(ft, new Point(2, r * m.CellHeight + (m.CellHeight - ft.Height) / 2));
        }
    }

    // ── 래핑 캐시 ──────────────────────────────────────────────────────────────

    private int[] GetWrapStarts(LogicalLine line, int cols) =>
        GetWrapStarts(line, cols, line.EffectiveLength());

    private int[] GetWrapStarts(LogicalLine line, int cols, int effLen)
    {
        var entry = _wrapCache.GetValue(line, _ => new WrapEntry { Cols = -1, Version = -1 });
        if (entry.Cols == cols && entry.Version == line.Version && entry.EffLen == effLen)
            return entry.Starts;

        // 유효 길이(effLen)까지만 래핑 → 후행 공백이 빈 시각 행을 만들지 않게 한다.
        // (linenoise getColumns 의 ESC[999C 가 남기는 대량 패딩 공백이 하단 빈 줄로 새는 문제 해결)
        var starts = new List<int> { 0 };
        int col = 0;
        for (int i = 0; i < effLen; i++)
        {
            int cw = CharWidth.Width(line[i].Ch);
            if (cw == 0) continue; // 결합 문자: 현재 행에 포함, 열 미증가
            if (col + cw > cols && col > 0)
            {
                starts.Add(i);
                col = 0;
            }
            col += cw;
        }

        entry.Cols = cols;
        entry.Version = line.Version;
        entry.EffLen = effLen;
        entry.Starts = starts.ToArray();
        return entry.Starts;
    }

    // ── 보이는 행 계산(가상화) ──────────────────────────────────────────────────

    private Snapshot BuildSnapshot(int cols, int rows)
    {
        var list = new List<VisRow>(rows);
        int topListIndex = 0;
        long revision, cursorAbs;
        int totalLines, cursorCol;

        lock (_buffer.SyncRoot)
        {
            revision = _buffer.Revision;
            totalLines = _buffer.LineCount;
            int curIdx = _buffer.CurrentLineIndex;
            cursorAbs = _buffer.TrimmedCount + curIdx;
            cursorCol = _buffer.GetLine(curIdx).CursorColumn;

            int lineCount = totalLines;
            if (_followTail)
            {
                // 바닥에서 위로 rows 개의 시각 행을 모은다
                var temp = new List<VisRow>(rows);
                for (int li = lineCount - 1; li >= 0 && temp.Count < rows; li--)
                {
                    var line = _buffer.GetLine(li);
                    int effLen = line.EffectiveLength();
                    var starts = GetWrapStarts(line, cols, effLen);
                    long abs = _buffer.TrimmedCount + li;
                    for (int r = starts.Length - 1; r >= 0 && temp.Count < rows; r--)
                        temp.Add(MakeVisRow(line, abs, starts, r, effLen));
                }
                temp.Reverse();
                list.AddRange(temp);
            }
            else
            {
                int li = (int)Math.Clamp(_topAbsLine - _buffer.TrimmedCount, 0, Math.Max(0, lineCount - 1));
                bool first = true;
                while (li < lineCount && list.Count < rows)
                {
                    var line = _buffer.GetLine(li);
                    int effLen = line.EffectiveLength();
                    var starts = GetWrapStarts(line, cols, effLen);
                    long abs = _buffer.TrimmedCount + li;
                    // 첫 라인의 시작 행은 현재 폭 기준 래핑 수로 clamp(리사이즈/트림 시 라인 누락 방지)
                    int r0 = first ? Math.Min(_topSubRow, Math.Max(0, starts.Length - 1)) : 0;
                    for (int r = r0; r < starts.Length && list.Count < rows; r++)
                        list.Add(MakeVisRow(line, abs, starts, r, effLen));
                    first = false;
                    li++;
                }
            }

            // 실제로 emit된 첫 행의 라인 인덱스를 스크롤바에 보고(스킵 시에도 정확)
            topListIndex = list.Count > 0 ? (int)(list[0].AbsLine - _buffer.TrimmedCount) : 0;
        }

        return new Snapshot(list.ToArray(), topListIndex, revision, totalLines, cursorAbs, cursorCol);
    }

    private VisRow MakeVisRow(LogicalLine line, long abs, int[] starts, int row, int effLen)
    {
        int start = starts[row];
        int end = row + 1 < starts.Length ? starts[row + 1] : effLen; // 마지막 행은 유효 길이까지(후행 공백 제외)
        int n = Math.Max(0, end - start);
        var cells = new Cell[n];
        for (int i = 0; i < n; i++)
            cells[i] = line[start + i];

        int startColumn = 0;
        for (int i = 0; i < start; i++)
            startColumn += CharWidth.Width(line[i].Ch);

        return new VisRow(abs, start, startColumn, line.Type, cells, line.Timestamp);
    }

    // ── 스크롤 앵커 이동(락 보유 전제) ──────────────────────────────────────────

    private void EnsureAnchorMaterialized()
    {
        if (!_followTail) return;
        // 현재 바닥 표시의 최상단 행을 앵커로 고정
        int cols = Math.Max(1, _columns);
        int rows = Math.Max(1, _rows);
        int lineCount = _buffer.LineCount;
        int collected = 0;
        long abs = _buffer.TrimmedCount + Math.Max(0, lineCount - 1);
        int sub = 0;
        for (int li = lineCount - 1; li >= 0 && collected < rows; li--)
        {
            var line = _buffer.GetLine(li);
            var starts = GetWrapStarts(line, cols);
            for (int r = starts.Length - 1; r >= 0 && collected < rows; r--)
            {
                abs = _buffer.TrimmedCount + li;
                sub = r;
                collected++;
            }
        }
        _topAbsLine = abs;
        _topSubRow = sub;
        _followTail = false;
    }

    private void MoveAnchorUp(int count)
    {
        int cols = Math.Max(1, _columns);
        for (int k = 0; k < count; k++)
        {
            if (_topSubRow > 0) { _topSubRow--; continue; }
            int li = (int)(_topAbsLine - _buffer.TrimmedCount);
            if (li <= 0) { _topSubRow = 0; break; }
            li--;
            var line = _buffer.GetLine(li);
            var starts = GetWrapStarts(line, cols);
            _topAbsLine = _buffer.TrimmedCount + li;
            _topSubRow = starts.Length - 1;
        }
    }

    private void MoveAnchorDown(int count)
    {
        int cols = Math.Max(1, _columns);
        int rows = Math.Max(1, _rows);
        for (int k = 0; k < count; k++)
        {
            int lineCount = _buffer.LineCount;
            // 트림으로 앵커가 유효 범위를 벗어났으면(li<0) 정규화 후 진행(스크롤 무반응 방지)
            int li = (int)Math.Clamp(_topAbsLine - _buffer.TrimmedCount, 0, Math.Max(0, lineCount - 1));
            _topAbsLine = _buffer.TrimmedCount + li;
            var line = _buffer.GetLine(li);
            var starts = GetWrapStarts(line, cols);
            if (_topSubRow < starts.Length - 1) { _topSubRow++; }
            else if (li < lineCount - 1)
            {
                _topAbsLine = _buffer.TrimmedCount + li + 1;
                _topSubRow = 0;
            }
            else break; // 이미 마지막 라인 마지막 행

            // 바닥에 도달하면 팔로우 재개
            if (IsAnchorAtTail(cols, rows, lineCount)) { _followTail = true; break; }
        }
    }

    private bool IsAnchorAtTail(int cols, int rows, int lineCount)
    {
        // 바닥 기준 top 앵커와 현재 앵커가 같은지(근사)
        int collected = 0;
        long tailAbs = _buffer.TrimmedCount + Math.Max(0, lineCount - 1);
        int tailSub = 0;
        for (int li = lineCount - 1; li >= 0 && collected < rows; li--)
        {
            var line = _buffer.GetLine(li);
            var starts = GetWrapStarts(line, cols);
            for (int r = starts.Length - 1; r >= 0 && collected < rows; r--)
            {
                tailAbs = _buffer.TrimmedCount + li;
                tailSub = r;
                collected++;
            }
        }
        return _topAbsLine >= tailAbs && (_topAbsLine > tailAbs || _topSubRow >= tailSub);
    }

    // ── 선택 / 히트 테스트 ─────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _lastDragPoint = p;
        var pos = HitTest(p);
        if (pos is null) return;
        _selAnchor = _selFocus = pos.Value;
        _hasSelection = false;
        _selecting = true;
        CaptureMouse();
        _forceRender = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;
        var p = e.GetPosition(this);
        _lastDragPoint = p;

        // 뷰 위/아래로 벗어나면 그 방향으로 자동 스크롤하며 선택을 계속 확장(한 화면을 넘는 드래그 선택).
        int dir = p.Y < 0 ? -1 : (p.Y > ActualHeight ? 1 : 0);
        if (dir != 0)
        {
            _autoScrollDir = dir;
            StartAutoScroll();
        }
        else
        {
            StopAutoScroll();
            UpdateSelectionFocusFromPoint(p);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;
        StopAutoScroll();
        ReleaseMouseCapture();
        if (_hasSelection)
        {
            var text = GetSelectedText();
            if (!string.IsNullOrEmpty(text))
                AutoCopyRequested?.Invoke(text!);
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selecting = false;
        StopAutoScroll();
    }

    // 드래그 좌표를 뷰 안으로 clamp 해 히트테스트하고 선택 초점을 갱신.
    private void UpdateSelectionFocusFromPoint(Point p)
    {
        double y = Math.Clamp(p.Y, 0, Math.Max(0, ActualHeight - 1));
        var pos = HitTest(new Point(p.X, y));
        if (pos is null) return;
        _selFocus = pos.Value;
        _hasSelection = _selFocus != _selAnchor;
        _forceRender = true;
    }

    private void StartAutoScroll()
    {
        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _autoScrollTimer.Tick += AutoScrollTick;
        }
        if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        _autoScrollDir = 0;
        _autoScrollTimer?.Stop();
    }

    private void AutoScrollTick(object? sender, EventArgs e)
    {
        if (!_selecting || _autoScrollDir == 0) { StopAutoScroll(); return; }
        ScrollByRows(_autoScrollDir * 2); // 방향당 2행씩(초점 확장은 ScrollByRows 내부에서 처리)
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        PasteRequested?.Invoke();
        e.Handled = true;
    }

    private (long Line, int Cell)? HitTest(Point p)
    {
        var m = _metrics;
        if (m is null || _visible.Length == 0) return null;
        int r = (int)(p.Y / m.CellHeight);
        r = Math.Clamp(r, 0, _visible.Length - 1);
        var vr = _visible[r];
        int targetCol = vr.StartColumn + Math.Max(0, (int)((p.X - _tsGutter) / m.CellWidth)); // gutter 보정

        // 행 내 셀 인덱스로 변환(폭은 원시값 — 렌더/wrap과 동일 기준)
        int col = vr.StartColumn;
        for (int i = 0; i < vr.Cells.Length; i++)
        {
            int cw = CharWidth.Width(vr.Cells[i].Ch);
            if (cw > 0 && targetCol < col + cw) return (vr.AbsLine, vr.StartCell + i);
            col += cw;
        }
        return (vr.AbsLine, vr.StartCell + vr.Cells.Length);
    }

    private static int RowWidth(in VisRow vr)
    {
        int w = 0;
        foreach (var c in vr.Cells) w += CharWidth.Width(c.Ch);
        return w;
    }

    private (( long Line, int Cell) min, (long Line, int Cell) max) NormalizeSelection()
    {
        var a = _selAnchor; var b = _selFocus;
        bool aFirst = a.Line < b.Line || (a.Line == b.Line && a.Cell <= b.Cell);
        return aFirst ? (a, b) : (b, a);
    }

    private static bool InSelection(long line, int cell, (long Line, int Cell) min, (long Line, int Cell) max)
    {
        if (line < min.Line || line > max.Line) return false;
        if (line == min.Line && cell < min.Cell) return false;
        if (line == max.Line && cell >= max.Cell) return false;
        return true;
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        _selecting = false;
        StopAutoScroll();
        _forceRender = true;
    }

    public string? GetSelectedText()
    {
        if (!_hasSelection) return null;
        var (min, max) = NormalizeSelection();
        var sb = new StringBuilder();
        lock (_buffer.SyncRoot)
        {
            long trimmed = _buffer.TrimmedCount;
            for (long abs = min.Line; abs <= max.Line; abs++)
            {
                int li = (int)(abs - trimmed);
                if (li < 0 || li >= _buffer.LineCount) continue;
                var line = _buffer.GetLine(li);
                int from = abs == min.Line ? min.Cell : 0;
                int to = abs == max.Line ? max.Cell : line.Count;
                from = Math.Clamp(from, 0, line.Count);
                to = Math.Clamp(to, 0, line.Count);
                for (int i = from; i < to; i++)
                    sb.Append(line[i].Ch);
                if (abs != max.Line)
                    sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    // ── 폰트/브러시/메트릭 ─────────────────────────────────────────────────────

    private FontMetrics? EnsureMetrics()
    {
        double dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
        if (_metrics is not null && Math.Abs(dpi - _metricsBuiltDpi) < 0.001 && Math.Abs(_fontSize - _metricsBuiltFontSize) < 0.001)
            return _metrics;

        _metrics = FontMetrics.Create(PrimaryFonts, FallbackFonts, _fontSize, (float)dpi);
        _metricsBuiltDpi = dpi;
        _metricsBuiltFontSize = _fontSize;
        return _metrics;
    }

    // 16색 팔레트+기본색은 소수지만 트루컬러(24bit)는 이론상 무한 → 캐시 상한으로 메모리 증가 방지
    private const int MaxCachedBrushes = 512;

    private SolidColorBrush GetBrush(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = new SolidColorBrush(c);
        b.Freeze();
        if (_brushes.Count >= MaxCachedBrushes)
            return b; // 상한 초과 시 캐시하지 않고 임시 브러시 반환(트루컬러 폭주 대비)
        _brushes[key] = b;
        return b;
    }
}
