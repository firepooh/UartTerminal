namespace UartTerminal.Core.Terminal;

/// <summary>
/// 논리 라인들의 순환 버퍼(스크롤백). 유일한 화면 상태 소스이며 <see cref="ITerminalSink"/>를 구현한다.
/// 모든 접근(변경/읽기)은 <see cref="SyncRoot"/> 락 아래에서 이뤄진다.
/// 변경 시 <see cref="Revision"/>이 증가하므로 렌더러는 이를 폴링해 다시 그릴지 판단한다(README §4.2 배칭).
/// </summary>
public sealed class TerminalBuffer : ITerminalSink
{
    public const int TabStop = 8;
    private const int CursorForwardCap = 4096; // linenoise 등의 대량 CUF로부터 메모리 보호

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
        _current.Print(ch, attr);
        Bump();
    }

    public void LineFeed()
    {
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
            _lines.Clear();
            _current = new LogicalLine();
            _lines.Add(_current);
            _trimmedCount = 0;
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

    private void TrimIfNeeded()
    {
        int over = _lines.Count - _maxLines;
        if (over > 0)
        {
            _lines.RemoveRange(0, over);
            _trimmedCount += over;
        }
    }
}
