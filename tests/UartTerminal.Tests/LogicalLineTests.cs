using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

/// <summary>
/// <see cref="LogicalLine.EffectiveLength"/> 회귀 테스트. 렌더러 soft-wrap 이 이 길이까지만 래핑하므로,
/// linenoise getColumns(ESC[999C) 가 남기는 대량 후행 공백이 하단 빈 줄로 새지 않아야 한다.
/// </summary>
public class LogicalLineTests
{
    private static LogicalLine Line(string s)
    {
        var l = new LogicalLine();
        foreach (char c in s) l.Print(c, CellAttributes.Default);
        return l;
    }

    [Fact]
    public void Empty_IsZero()
    {
        Assert.Equal(0, new LogicalLine().EffectiveLength());
    }

    [Fact]
    public void NoTrailingSpaces_IsFullContent()
    {
        var l = Line("hello");           // cursor=5, count=5
        Assert.Equal(5, l.EffectiveLength());
    }

    [Fact]
    public void CursorAtEnd_IncludesTrailingSpace()
    {
        // 프롬프트 "xcp> " 는 후행 공백을 갖지만 커서가 그 끝(col 5)에 있으므로 커서까지 포함해야 함
        var l = Line("xcp> ");           // 'x','c','p','>',' ' → cursor=5, count=5
        Assert.Equal(5, l.EffectiveLength());
    }

    [Fact]
    public void TrailingDefaultSpaces_BeyondCursor_AreTrimmed()
    {
        var l = Line("hi   ");           // "hi" + 3 spaces, cursor=5, count=5
        l.Backspace(); l.Backspace(); l.Backspace(); // cursor→2
        // 마지막 비공백 'i'(idx1) → last+1=2, 커서=2 → 유효 길이 2 (후행 3공백 제외)
        Assert.Equal(2, l.EffectiveLength());
        Assert.Equal(5, l.Count);        // 셀 자체는 그대로(트리밍은 표시 계산에만)
    }

    [Fact]
    public void LinenoisePadding_Scenario_TrimmedButCursorKept()
    {
        // "xcp> " + 100개의 패딩 공백을 만든 뒤(ESC[999C 흉내), 커서를 col 5 로 되돌린다(ESC[999D + 프롬프트 재출력).
        var l = Line("xcp> ");
        for (int i = 0; i < 100; i++) l.Print(' ', CellAttributes.Default); // cursor=105, count=105
        l.CarriageReturn();                                                 // cursor→0
        for (int i = 0; i < 5; i++) l.AdvanceCursorOverExisting();          // cursor→5 ("xcp> " 뒤)
        Assert.Equal(105, l.Count);
        Assert.Equal(5, l.EffectiveLength());  // 100개 후행 공백은 래핑에서 제외, 커서(5) 보존
    }

    [Fact]
    public void ColoredTrailingSpace_IsPreserved()
    {
        var l = new LogicalLine();
        l.Print('h', CellAttributes.Default);
        l.Print('i', CellAttributes.Default);
        // 배경색이 있는 공백(예: 상태바) → 화면에 보이므로 트리밍 금지
        var bg = CellAttributes.Default.WithBackground(TermColor.FromPalette(4));
        l.Print(' ', bg);                // cursor=3, count=3
        l.Backspace();                   // cursor→2
        Assert.Equal(3, l.EffectiveLength()); // 색 공백(idx2)이 마지막 보이는 셀 → 유효 길이 3
    }

    [Fact]
    public void ReverseTrailingSpace_IsPreserved()
    {
        var l = new LogicalLine();
        l.Print('a', CellAttributes.Default);
        var rev = CellAttributes.Default.AddFlag(CellFlags.Reverse);
        l.Print(' ', rev);               // reverse 공백은 보임
        l.CarriageReturn();              // cursor→0
        Assert.Equal(2, l.EffectiveLength()); // reverse 공백(idx1) 보존 → 2
    }

    [Fact]
    public void AllDefaultSpaces_CursorAtStart_IsZero()
    {
        var l = Line("     ");           // 공백 5개, cursor=5
        l.CarriageReturn();              // cursor→0
        Assert.Equal(0, l.EffectiveLength()); // 전부 후행 공백 + 커서 0 → 0
    }
}
