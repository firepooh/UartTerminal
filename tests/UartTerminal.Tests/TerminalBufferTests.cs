using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

public class TerminalBufferTests
{
    private static void Print(TerminalBuffer b, string s)
    {
        foreach (char c in s)
            b.Print(c, CellAttributes.Default);
    }

    [Fact]
    public void ClearScreen_PreservesScrollback()
    {
        var b = new TerminalBuffer(1000);
        Print(b, "boot log");
        b.LineFeed();
        int before = b.LineCount;

        b.ClearScreen(5);

        // 이전 내용은 스크롤백에 그대로 남아 있어야 함
        Assert.Equal("boot log", b.GetLine(0).Text());
        Assert.True(b.LineCount >= before + 5);
    }

    [Fact]
    public void Clear_WipesEverythingIncludingScrollback()
    {
        var b = new TerminalBuffer(1000);
        Print(b, "line1");
        b.LineFeed();
        Print(b, "line2");

        b.Clear();

        Assert.Equal(1, b.LineCount);
        Assert.Equal("", b.GetLine(0).Text());
    }

    /// <summary>
    /// 절대 라인 번호는 <b>Clear 후에도 단조 증가</b>해야 한다. 예전에는 <c>_trimmedCount = 0</c> 으로
    /// 되돌려 번호를 재사용했고, [버퍼 지우기] 는 검색 결과(절대 번호로 저장됨)를 지우지 않으므로
    /// 하이라이트가 엉뚱한 줄에 남았다. "트림에도 안정" 이라는 불변식 자체가 깨져 있었다.
    /// </summary>
    [Fact]
    public void Clear_KeepsAbsoluteLineNumbersMonotonic()
    {
        var b = new TerminalBuffer(1000);
        Print(b, "line1"); b.LineFeed();
        Print(b, "line2"); b.LineFeed();
        long before = b.TrimmedCount + b.LineCount;   // 다음에 쓰일 절대 번호

        b.Clear();

        Assert.True(b.TrimmedCount >= before - 1,
            $"Clear 후 절대 번호가 되돌아갔습니다: TrimmedCount={b.TrimmedCount}, 이전 최대={before}");
    }

    /// <summary>
    /// 셀 총량 상한이 실제로 버퍼를 자른다. 줄 수 상한만 있던 동안에는 긴 줄이 섞이면
    /// (개행 모드 불일치 등) 메모리가 사실상 묶이지 않았다.
    /// </summary>
    [Fact]
    public void TotalCellBudget_TrimsEvenWhenLineCountIsUnderLimit()
    {
        var b = new TerminalBuffer(1_000_000);   // 줄 수 상한은 사실상 없음
        var chunk = new string('x', TerminalBuffer.MaxLineCells);

        // 총량 상한의 3배를 넣는다
        int rounds = (int)(TerminalBuffer.MaxTotalCells / TerminalBuffer.MaxLineCells) * 3;
        for (int i = 0; i < rounds; i++) { Print(b, chunk); b.LineFeed(); }

        long cells = 0;
        for (int i = 0; i < b.LineCount; i++) cells += b.GetLine(i).Count;

        Assert.True(cells <= TerminalBuffer.MaxTotalCells + TerminalBuffer.MaxLineCells,
            $"보관 셀 {cells:N0} 이 총량 상한 {TerminalBuffer.MaxTotalCells:N0} 을 넘었습니다");
        Assert.True(b.TrimmedCount > 0, "총량 초과인데도 아무 줄도 버리지 않았습니다");
    }

    [Theory]
    [InlineData('A', 1)]
    [InlineData('0', 1)]
    [InlineData('가', 2)]      // 한글 음절: 전각 2셀
    [InlineData('中', 2)]      // CJK
    [InlineData('́', 0)]  // 결합 악센트: 제로폭
    [InlineData('​', 0)]  // ZWSP
    public void CharWidth_MatchesEastAsianWidth(char ch, int expected)
    {
        Assert.Equal(expected, CharWidth.Width(ch));
    }

    [Fact]
    public void DisplayWidth_CountsWideAndZeroWidthCorrectly()
    {
        var line = new LogicalLine();
        // "e" + 결합 악센트 + "가" → 1 + 0 + 2 = 3
        line.Print('e', CellAttributes.Default);
        line.Print('́', CellAttributes.Default);
        line.Print('가', CellAttributes.Default);
        Assert.Equal(3, line.DisplayWidth);
    }
}
