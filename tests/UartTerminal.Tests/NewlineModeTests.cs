using System.Text;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

/// <summary>
/// 개행 규약(New-line) 회귀 테스트. 수신 모드는 "어느 바이트를 개행으로 볼지"의 선택이며,
/// 기본(CR+LF)은 기존 동작(개행=LF, CR=줄 처음으로)을 그대로 유지해야 한다.
/// </summary>
public class NewlineModeTests
{
    private static TerminalEngine NewEngine(ReceiveNewline mode) =>
        new(new UTF8Encoding(false)) { ReceiveNewline = mode };

    private static void Feed(TerminalEngine e, string ascii) => e.Receive(Encoding.UTF8.GetBytes(ascii));

    private static string[] Lines(TerminalEngine e)
    {
        lock (e.Buffer.SyncRoot)
        {
            var list = new List<string>();
            for (int i = 0; i < e.Buffer.LineCount; i++) list.Add(e.Buffer.GetLine(i).Text());
            return list.ToArray();
        }
    }

    // ── 기본(CR+LF): 개행=LF, CR=줄 처음으로 ────────────────────────────────────

    [Fact]
    public void CrLf_Default_CrLfIsOneLine()
    {
        var e = NewEngine(ReceiveNewline.CrLf);
        Feed(e, "a\r\nb");
        Assert.Equal(new[] { "a", "b" }, Lines(e));
    }

    [Fact]
    public void CrLf_Default_BareLfBreaksLine()
    {
        var e = NewEngine(ReceiveNewline.CrLf);
        Feed(e, "a\nb");
        Assert.Equal(new[] { "a", "b" }, Lines(e));
    }

    [Fact]
    public void CrLf_Default_BareCrOverwritesSameLine()
    {
        // 진행바(\r 로 같은 줄 갱신) 동작이 유지되어야 한다 — 기본 모드의 핵심 가치.
        var e = NewEngine(ReceiveNewline.CrLf);
        Feed(e, "50%\r60%");
        Assert.Equal(new[] { "60%" }, Lines(e));
    }

    // ── LF 모드: 개행=LF, CR 무시 ───────────────────────────────────────────────

    [Fact]
    public void LfMode_IgnoresCr_NoOverwrite()
    {
        var e = NewEngine(ReceiveNewline.Lf);
        Feed(e, "50%\r60%");
        Assert.Equal(new[] { "50%60%" }, Lines(e));
    }

    [Fact]
    public void LfMode_CrLfIsOneLine()
    {
        var e = NewEngine(ReceiveNewline.Lf);
        Feed(e, "a\r\nb");
        Assert.Equal(new[] { "a", "b" }, Lines(e));
    }

    // ── CR 모드: 개행=CR, LF 무시 ───────────────────────────────────────────────

    [Fact]
    public void CrMode_BareCrBreaksLine()
    {
        var e = NewEngine(ReceiveNewline.Cr);
        Feed(e, "a\rb");
        Assert.Equal(new[] { "a", "b" }, Lines(e));
    }

    [Fact]
    public void CrMode_CrLfIsOneLine_LfAbsorbed()
    {
        var e = NewEngine(ReceiveNewline.Cr);
        Feed(e, "a\r\nb");
        Assert.Equal(new[] { "a", "b" }, Lines(e));
    }

    [Fact]
    public void CrMode_BareLfIsIgnored()
    {
        var e = NewEngine(ReceiveNewline.Cr);
        Feed(e, "a\nb");
        Assert.Equal(new[] { "ab" }, Lines(e));
    }

    // ── AUTO: CR/LF/CR+LF/LF+CR 모두 개행 1회(TeraTerm 규칙) ────────────────────

    [Fact]
    public void Auto_CrLfPair_IsOneLine()
    {
        var e = NewEngine(ReceiveNewline.Auto);
        Feed(e, "a\r\nb\r\nc");
        Assert.Equal(new[] { "a", "b", "c" }, Lines(e));
    }

    [Fact]
    public void Auto_LfCrPair_IsOneLine()
    {
        var e = NewEngine(ReceiveNewline.Auto);
        Feed(e, "a\n\rb\n\rc");
        Assert.Equal(new[] { "a", "b", "c" }, Lines(e));
    }

    [Fact]
    public void Auto_BareCrOrLf_EachBreaksLine()
    {
        var e = NewEngine(ReceiveNewline.Auto);
        Feed(e, "a\rb\nc");
        Assert.Equal(new[] { "a", "b", "c" }, Lines(e));
    }

    [Fact]
    public void Auto_DoubleCr_IsTwoLines()
    {
        // 붙어 있는 CR/LF '쌍'만 합친다 — CR CR 은 빈 줄을 만든다(TeraTerm 문서의 예와 동일).
        var e = NewEngine(ReceiveNewline.Auto);
        Feed(e, "a\r\rb");
        Assert.Equal(new[] { "a", "", "b" }, Lines(e));
    }

    [Fact]
    public void Auto_SplitChunks_PairStillCollapses()
    {
        // CR 과 LF 가 서로 다른 수신 청크로 쪼개져 도착해도 한 번의 개행이어야 한다(파서 상태 유지).
        var e = NewEngine(ReceiveNewline.Auto);
        Feed(e, "a\r");
        Feed(e, "\nb");
        Assert.Equal(new[] { "a", "b" }, Lines(e));
    }

    [Fact]
    public void Auto_CrThenTextThenLf_IsTwoBreaks()
    {
        // 쌍 추적은 '붙어 있을 때'만 — 사이에 문자가 끼면 각각 개행이다.
        var e = NewEngine(ReceiveNewline.Auto);
        Feed(e, "a\rb\nc");
        Assert.Equal(3, Lines(e).Length);
    }

    // ── 모드 전환 / 송신 개행 ───────────────────────────────────────────────────

    [Fact]
    public void ModeSwitch_AppliesToSubsequentData()
    {
        var e = NewEngine(ReceiveNewline.CrLf);
        Feed(e, "a\rb");                        // 덮어쓰기 → "b"
        e.ReceiveNewline = ReceiveNewline.Cr;
        Feed(e, "\rc");                         // 이제 CR 이 개행
        Assert.Equal(new[] { "b", "c" }, Lines(e));
    }

    [Theory]
    [InlineData(TransmitNewline.Cr, new byte[] { 0x0D })]
    [InlineData(TransmitNewline.CrLf, new byte[] { 0x0D, 0x0A })]
    [InlineData(TransmitNewline.Lf, new byte[] { 0x0A })]
    public void TransmitNewline_Bytes(TransmitNewline mode, byte[] expected)
        => Assert.Equal(expected, mode.Bytes());

    [Theory]
    [InlineData(TransmitNewline.Cr, "\r")]
    [InlineData(TransmitNewline.CrLf, "\r\n")]
    [InlineData(TransmitNewline.Lf, "\n")]
    public void TransmitNewline_Text(TransmitNewline mode, string expected)
        => Assert.Equal(expected, mode.Text());
}
