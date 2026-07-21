using System.Text;

namespace UartTerminal.Core.Terminal;

/// <summary>라인의 성격. AI(MCP) 송신 에코는 일반 수신 데이터와 구분해 표시/취급한다(Phase B 훅).</summary>
public enum LineType : byte
{
    /// <summary>시리얼로 수신된 일반 데이터.</summary>
    Normal,
    /// <summary>AI(MCP)가 송신한 데이터의 화면 표시용 메타 라인.</summary>
    AiEcho
}

/// <summary>
/// 개행으로 구분된 하나의 논리 라인. 화면 "행"이 아니라 논리 단위로 저장하며,
/// 렌더 시점에 현재 폭으로 soft-wrap 된다(README §4.1). 마지막(열린) 라인은
/// CR/BS/ESC[K 로 라인 내 편집이 가능해 esp_console(linenoise) REPL 을 지원한다.
/// </summary>
public sealed class LogicalLine
{
    private readonly List<Cell> _cells;

    public LineType Type { get; set; }

    /// <summary>내용이 바뀔 때마다 증가. soft-wrap 캐시 무효화 키로 사용.</summary>
    public int Version { get; private set; }

    /// <summary>열린 라인의 커서 셀 인덱스(라인 내 편집/커서 렌더용). 확정된 라인에서는 의미 없음.</summary>
    public int Cursor { get; private set; }

    public LogicalLine(LineType type = LineType.Normal)
    {
        _cells = new List<Cell>();
        Type = type;
        Cursor = 0;
        Version = 0;
    }

    public int Count => _cells.Count;
    public Cell this[int index] => _cells[index];
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>전체 표시 폭(셀 수 합). 전각 문자는 2로 계산.</summary>
    public int DisplayWidth
    {
        get
        {
            int w = 0;
            for (int i = 0; i < _cells.Count; i++)
                w += CharWidth.Width(_cells[i].Ch);
            return w;
        }
    }

    /// <summary>커서의 열 위치(커서 앞 셀들의 폭 합).</summary>
    public int CursorColumn
    {
        get
        {
            int w = 0;
            int end = Math.Min(Cursor, _cells.Count);
            for (int i = 0; i < end; i++)
                w += CharWidth.Width(_cells[i].Ch);
            return w;
        }
    }

    /// <summary>커서 위치에 문자를 쓰고 커서를 전진(덮어쓰기 또는 확장).</summary>
    public void Print(char ch, CellAttributes attr)
    {
        var cell = new Cell(ch, attr);
        if (Cursor < _cells.Count)
            _cells[Cursor] = cell;
        else
            _cells.Add(cell);
        Cursor++;
        Version++;
    }

    /// <summary>CR: 커서를 라인 시작으로. 내용은 유지(이후 Print가 덮어씀).</summary>
    public void CarriageReturn() => Cursor = 0;

    /// <summary>BS: 커서를 한 셀 뒤로(비파괴).</summary>
    public void Backspace()
    {
        if (Cursor > 0) Cursor--;
    }

    /// <summary>기존 셀 위에서 커서를 한 셀 전진(내용 유지). 전진하면 그 셀의 표시 폭, 끝이면 -1.</summary>
    public int AdvanceCursorOverExisting()
    {
        if (Cursor < _cells.Count)
        {
            int w = CharWidth.Width(_cells[Cursor].Ch);
            Cursor++;
            return w;
        }
        return -1;
    }

    /// <summary>ESC[0K: 커서부터 라인 끝까지 지움.</summary>
    public void EraseToEnd()
    {
        if (Cursor < _cells.Count)
        {
            _cells.RemoveRange(Cursor, _cells.Count - Cursor);
            Version++;
        }
    }

    /// <summary>ESC[1K: 라인 시작부터 커서까지 공백으로 지움(위치 보존).</summary>
    public void EraseToStart()
    {
        int end = Math.Min(Cursor, _cells.Count);
        for (int i = 0; i < end; i++)
            _cells[i] = new Cell(' ', CellAttributes.Default);
        if (end > 0) Version++;
    }

    /// <summary>ESC[2K: 라인 전체 삭제.</summary>
    public void EraseAll()
    {
        if (_cells.Count > 0)
        {
            _cells.Clear();
            Version++;
        }
        Cursor = 0;
    }

    /// <summary>복사/로그용 문자열(soft-wrap 개행 없이 논리 라인 전체를 한 줄로).</summary>
    public string Text()
    {
        var sb = new StringBuilder(_cells.Count);
        for (int i = 0; i < _cells.Count; i++)
            sb.Append(_cells[i].Ch);
        return sb.ToString();
    }
}
