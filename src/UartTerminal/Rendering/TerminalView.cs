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
        public VisRow(long absLine, int startCell, int startColumn, LineType type, Cell[] cells)
        {
            AbsLine = absLine; StartCell = startCell; StartColumn = startColumn; Type = type; Cells = cells;
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

    private static readonly string[] PrimaryFonts = { "Cascadia Mono", "D2Coding", "Consolas", "Courier New" };
    private static readonly string[] FallbackFonts = { "Malgun Gothic", "맑은 고딕", "Gulim", "굴림" };

    private readonly TerminalBuffer _buffer;
    private readonly TerminalPalette _palette = TerminalPalette.Dark;
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

        _columns = Math.Max(1, (int)(w / metrics.CellWidth));
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

            for (int r = 0; r < _visible.Length; r++)
            {
                double y = r * metrics.CellHeight;
                RenderRow(dc, metrics, _visible[r], y, selMin, selMax);
            }

            if (_followTail)
                DrawCursor(dc, metrics, snap.CursorAbs, snap.CursorCol);

            // 개정 번호는 스냅샷과 동일 락 구간에서 캡처됨 → 락 밖 재읽기로 인한 stale(누락) 방지
            _lastRevision = snap.Revision;
            _forceRender = false;

            ScrollMetricsChanged?.Invoke(new ScrollMetrics(snap.TotalLines, snap.TopListIndex, _rows, _followTail));
        }
        catch (Exception ex)
        {
            // 렌더 파이프라인 예외가 앱을 종료시키지 않도록 방어
            UartTerminal.DiagLog.Exception("OnRender", ex);
        }
    }

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
                bool selected = _hasSelection && InSelection(row.AbsLine, row.StartCell + i, selMin, selMax);
                if (selected)
                {
                    dc.DrawRectangle(GetBrush(_palette.SelectionBackground), null,
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

    // ── 래핑 캐시 ──────────────────────────────────────────────────────────────

    private int[] GetWrapStarts(LogicalLine line, int cols) =>
        GetWrapStarts(line, cols, EffectiveLength(line));

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

    /// <summary>
    /// 표시상 의미 없는 후행 공백을 제외한 유효 셀 수(단, 커서 위치는 항상 포함해 커서가 잘리지 않게 함).
    /// 색 배경/reverse 가 있는 공백은 시각적으로 보이므로 트리밍하지 않는다.
    /// </summary>
    private static int EffectiveLength(LogicalLine line)
    {
        int last = -1;
        for (int i = line.Count - 1; i >= 0; i--)
        {
            var c = line[i];
            bool trimmable = c.Ch == ' '
                && c.Attributes.Background.Kind == ColorKind.Default
                && !c.Attributes.Flags.HasFlag(CellFlags.Reverse);
            if (!trimmable) { last = i; break; }
        }
        int eff = Math.Max(last + 1, line.Cursor);
        return Math.Clamp(eff, 0, line.Count);
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
                    int effLen = EffectiveLength(line);
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
                    int effLen = EffectiveLength(line);
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

        return new VisRow(abs, start, startColumn, line.Type, cells);
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
        var pos = HitTest(e.GetPosition(this));
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
        var pos = HitTest(e.GetPosition(this));
        if (pos is null) return;
        _selFocus = pos.Value;
        _hasSelection = _selFocus != _selAnchor;
        _forceRender = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        if (_hasSelection)
        {
            var text = GetSelectedText();
            if (!string.IsNullOrEmpty(text))
                AutoCopyRequested?.Invoke(text!);
        }
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
        int targetCol = vr.StartColumn + Math.Max(0, (int)(p.X / m.CellWidth));

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
