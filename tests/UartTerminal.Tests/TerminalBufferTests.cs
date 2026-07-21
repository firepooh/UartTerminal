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
        Assert.Equal(0, b.TrimmedCount);
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
