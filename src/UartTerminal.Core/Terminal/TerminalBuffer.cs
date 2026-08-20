namespace UartTerminal.Core.Terminal;

/// <summary>
/// 논리 라인들의 스크롤백. 유일한 화면 상태 소스이며 <see cref="ITerminalSink"/>를 구현한다.
/// 자료구조는 <b>순환 버퍼가 아니라</b> 가득 차면 앞을 잘라내는 <see cref="List{T}"/> 다(<see cref="Drop"/>) —
/// 줄마다 전체 시프트가 일어난다. 115200bps 실사용에서는 무시할 수준이지만
/// <c>maxLines</c> 를 크게(수만 줄) 올릴 생각이면 여기부터 링 버퍼로 바꿔야 한다.
/// 모든 접근(변경/읽기)은 <see cref="SyncRoot"/> 락 아래에서 이뤄진다.
/// 변경 시 <see cref="Revision"/>이 증가하므로 렌더러는 이를 폴링해 다시 그릴지 판단한다(README §4.2 배칭).
/// </summary>
public sealed class TerminalBuffer : ITerminalSink
{
    public const int TabStop = 8;
    private const int CursorForwardCap = 4096; // linenoise 등의 대량 CUF로부터 메모리 보호

    /// <summary>
    /// 논리 라인 하나의 최대 셀 수. 넘으면 강제로 줄을 바꾼다.
    ///
    /// 상한이 없던 동안에는 <b>개행이 오지 않는 스트림에서 한 줄이 무한히 자랐다</b> —
    /// 수신 개행 모드가 장치와 어긋난 경우(Rx=CR 인데 장치는 LF 만 보냄), 잘못된 baud 로 열어
    /// 임의 바이트가 들어오는 경우, 바이너리 스트림. 측정값: 8MB 입력 → 한 줄 7,000,000셀 ·
    /// 힙 112MB(입력의 14배). <see cref="_maxLines"/> 는 줄 <i>수</i>만 제한하므로 소용이 없었다.
    /// 강제 개행을 넣으면 스크롤백 상한이 다시 총량을 제한한다(최악 = maxLines × 이 값).
    /// </summary>
    public const int MaxLineCells = CursorForwardCap;

    /// <summary>
    /// 보관하는 셀 총량 상한(≈ 이 값 × 16바이트). 줄 수 상한만으로는 메모리가 묶이지 않는다 —
    /// <see cref="MaxLineCells"/> 를 넣은 뒤에도 8MB 입력이 1,709줄 × 4,096셀로 흩어져 힙 94MB 를 썼다
    /// (최악 = maxLines × MaxLineCells = 4천만 셀 ≈ 650MB). 총량으로 잘라야 실제로 묶인다.
    /// 평범한 로그(줄당 60~120자)로는 10,000줄을 다 채워도 이 값에 닿지 않으므로 체감되지 않는다.
    /// </summary>
    public const long MaxTotalCells = 4_000_000;   // ≈ 64MB

    /// <summary>확정된(더 이상 자라지 않는) 라인들의 셀 수 합. 총량 상한 판단용.</summary>
    private long _closedCells;

    private readonly object _sync = new();
    private readonly List<LogicalLine> _lines = new();
    private readonly int _maxLines;
    private LogicalLine _current;
    private long _revision;
    private long _trimmedCount;

    public TerminalBuffer(int maxLines = 10_000)
    {
        _maxLines = Math.Max(100, maxLines);
        _current = new LogicalLine();
        _lines.Add(_current);
    }

    /// <summary>변경/읽기 시 잡아야 하는 락.</summary>
    public object SyncRoot => _sync;

    /// <summary>변경마다 증가하는 개정 번호(렌더러 폴링용).</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>현재 라인 수(락 안에서 읽을 것).</summary>
    public int LineCount => _lines.Count;

    /// <summary>버퍼 시작 이후 앞에서 폐기된 라인 총 수(절대 라인 번호 계산용).</summary>
    public long TrimmedCount => _trimmedCount;

    /// <summary>인덱스로 라인 접근(락 안에서 사용).</summary>
    public LogicalLine GetLine(int index) => _lines[index];

    /// <summary>현재(열린) 라인의 인덱스.</summary>
    public int CurrentLineIndex => _lines.Count - 1;

    private void Bump() => Interlocked.Increment(ref _revision);

    // ── ITerminalSink (호출자가 SyncRoot 락 보유 전제) ──────────────────────────

    public void Print(char ch, CellAttributes attr)
    {
        // 커서 기준으로 판단한다 — CR 로 되돌아가 덮어쓰는 중(진행률 표시 등)에는 줄이 자라지 않으므로
        // 강제 개행을 하면 안 된다. 새 셀을 덧붙이려는 순간에만 상한이 걸린다.
        if (_current.Cursor >= MaxLineCells)
            LineFeed();
        _current.Print(ch, attr);
        Bump();
    }

    public void LineFeed()
    {
        // 방금 닫힌 라인은 이제 자라지 않는다 → 총량 집계에 반영한다.
        _closedCells += _current.Count;
        _current = new LogicalLine();
        _lines.Add(_current);
        TrimIfNeeded();
        Bump();
    }

    public void CarriageReturn()
    {
        _current.CarriageReturn();
        // 커서만 이동(내용 불변)하지만 커서 렌더링 갱신을 위해 개정 증가
        Bump();
    }

    public void Backspace()
    {
        _current.Backspace();
        Bump();
    }

    public void HorizontalTab(CellAttributes attr)
    {
        int col = _current.CursorColumn;
        int next = ((col / TabStop) + 1) * TabStop;
        int pad = next - col;
        for (int i = 0; i < pad; i++)
            _current.Print(' ', attr);
        Bump();
    }

    public void CursorForward(int n, CellAttributes attr)
    {
        if (n <= 0) n = 1;
        int col = _current.CursorColumn;
        int target = Math.Min(col + n, CursorForwardCap);
        AdvanceCursorTo(target, attr);
        Bump();
    }

    public void CursorBack(int n)
    {
        if (n <= 0) n = 1;
        for (int i = 0; i < n; i++)
            _current.Backspace();
        Bump();
    }

    public void CursorColumnAbsolute(int col, CellAttributes attr)
    {
        if (col < 1) col = 1;
        int target = Math.Min(col - 1, CursorForwardCap);
        _current.CarriageReturn(); // 커서를 0으로
        AdvanceCursorTo(target, attr);
        Bump();
    }

    /// <summary>커서를 목표 열까지 전진(기존 셀 위로 이동, 부족분은 공백 패딩).</summary>
    private void AdvanceCursorTo(int targetColumn, CellAttributes attr)
    {
        int col = _current.CursorColumn;
        while (col < targetColumn)
        {
            int w = _current.AdvanceCursorOverExisting();
            if (w >= 0)
                col += Math.Max(w, 1);
            else
            {
                _current.Print(' ', attr);
                col += 1;
            }
        }
    }

    public void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: _current.EraseToEnd(); break;
            case 1: _current.EraseToStart(); break;
            case 2: _current.EraseAll(); break;
        }
        Bump();
    }

    public void EraseInDisplay(int mode)
    {
        // 로그 모델: 스크롤백 보존. 현재 라인이 비어있지 않으면 새 라인으로 이동.
        if (_current.Count > 0)
            LineFeed();
    }

    public void Bell() { /* Phase A: 무시 */ }

    public (int Row, int Col) GetCursorPosition() => (1, _current.CursorColumn + 1);

    // ── 로컬(사용자) 조작 ──────────────────────────────────────────────────────

    /// <summary>
    /// 사용자 "Clear screen": 스크롤백을 보존하면서 화면을 비운다. 뷰포트 높이만큼 빈 라인을 넣어
    /// 기존 내용을 위로 밀어내므로, 위로 스크롤하면 이전 로그(부팅/크래시 기록 등)를 여전히 볼 수 있다.
    /// </summary>
    public void ClearScreen(int viewportRows)
    {
        int n = Math.Clamp(viewportRows, 1, 1000);
        lock (_sync)
        {
            for (int i = 0; i < n; i++)
            {
                _current = new LogicalLine();
                _lines.Add(_current);
            }
            TrimIfNeeded();
            Bump();
        }
    }

    /// <summary>사용자 "Clear buffer": 스크롤백 포함 전체 삭제.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            // 절대 라인 번호는 단조 증가를 유지한다 — 0 으로 되돌리면 번호가 재사용되어
            // 검색 하이라이트(절대 번호로 저장)가 엉뚱한 줄에 남았다.
            _trimmedCount += _lines.Count;
            _lines.Clear();
            _current = new LogicalLine();
            _lines.Add(_current);
            _closedCells = 0;
            Bump();
        }
    }

    /// <summary>AI(MCP) 송신 데이터를 메타 라인으로 삽입(Phase B 훅). 현재 라인이 비어있지 않으면 개행 후 삽입.</summary>
    public void AppendMetaLine(string text)
    {
        lock (_sync)
        {
            if (_current.Count > 0)
                LineFeed();
            _current.Type = LineType.AiEcho;
            foreach (char ch in text)
                _current.Print(ch, CellAttributes.Default);
            LineFeed();
        }
    }

    /// <summary>
    /// 앞에서부터 라인을 버려 <b>줄 수</b>와 <b>셀 총량</b> 두 상한을 모두 지킨다.
    /// 현재(열린) 라인은 항상 남긴다.
    /// </summary>
    private void TrimIfNeeded()
    {
        int over = _lines.Count - _maxLines;
        if (over > 0)
            Drop(over);

        // 셀 총량 초과 — 긴 줄이 섞이면 줄 수 상한보다 이쪽이 먼저 걸린다.
        // 버릴 개수만 먼저 세고(집계는 Drop 이 담당) 현재(열린) 라인은 항상 남긴다.
        int n = 0;
        long freed = 0;
        while (_closedCells - freed > MaxTotalCells && n < _lines.Count - 1)
        {
            freed += _lines[n].Count;
            n++;
        }
        if (n > 0)
            Drop(n);
    }

    /// <summary>앞에서 <paramref name="count"/> 줄을 버리고 절대 라인 번호·셀 집계를 맞춘다.</summary>
    private void Drop(int count)
    {
        for (int i = 0; i < count; i++)
            _closedCells -= _lines[i].Count;
        if (_closedCells < 0) _closedCells = 0;   // 방어(열린 라인이 섞이는 경우)
        _lines.RemoveRange(0, count);
        _trimmedCount += count;
    }
}
