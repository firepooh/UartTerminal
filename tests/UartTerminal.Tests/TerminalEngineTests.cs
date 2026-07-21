using System.Text;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

public class TerminalEngineTests
{
    private const string ESC = "";

    private static TerminalEngine NewEngine() => new(new UTF8Encoding(false));

    private static void Feed(TerminalEngine e, string ascii) =>
        e.Receive(Encoding.UTF8.GetBytes(ascii));

    private static string LineText(TerminalEngine e, int index)
    {
        lock (e.Buffer.SyncRoot)
            return e.Buffer.GetLine(index).Text();
    }

    private static int LineCount(TerminalEngine e)
    {
        lock (e.Buffer.SyncRoot)
            return e.Buffer.LineCount;
    }

    [Fact]
    public void PlainText_AppendsToCurrentLine()
    {
        var e = NewEngine();
        Feed(e, "hello");
        Assert.Equal(1, LineCount(e));
        Assert.Equal("hello", LineText(e, 0));
    }

    [Fact]
    public void LineFeed_StartsNewLine()
    {
        var e = NewEngine();
        Feed(e, "a\nb");
        Assert.Equal(2, LineCount(e));
        Assert.Equal("a", LineText(e, 0));
        Assert.Equal("b", LineText(e, 1));
    }

    [Fact]
    public void CarriageReturn_OverwritesFromStart()
    {
        var e = NewEngine();
        Feed(e, "abc\rX");
        Assert.Equal("Xbc", LineText(e, 0));
    }

    [Fact]
    public void Backspace_MovesCursorBack()
    {
        var e = NewEngine();
        Feed(e, "abc\bX");
        Assert.Equal("abX", LineText(e, 0));
    }

    [Fact]
    public void Sgr_SetsForegroundColor()
    {
        var e = NewEngine();
        Feed(e, ESC + "[32mA");
        lock (e.Buffer.SyncRoot)
        {
            var cell = e.Buffer.GetLine(0)[0];
            Assert.Equal('A', cell.Ch);
            Assert.Equal(ColorKind.Palette, cell.Attributes.Foreground.Kind);
            Assert.Equal(2, cell.Attributes.Foreground.Index); // 32 = green = palette 2
        }
    }

    [Fact]
    public void Sgr_ResetRestoresDefault()
    {
        var e = NewEngine();
        Feed(e, ESC + "[31mA" + ESC + "[0mB");
        lock (e.Buffer.SyncRoot)
        {
            var a = e.Buffer.GetLine(0)[0];
            var b = e.Buffer.GetLine(0)[1];
            Assert.Equal(ColorKind.Palette, a.Attributes.Foreground.Kind);
            Assert.Equal(1, a.Attributes.Foreground.Index);
            Assert.Equal(ColorKind.Default, b.Attributes.Foreground.Kind);
        }
    }

    [Fact]
    public void Sgr_BrightForeground()
    {
        var e = NewEngine();
        Feed(e, ESC + "[92mA"); // bright green = palette 10
        lock (e.Buffer.SyncRoot)
        {
            var a = e.Buffer.GetLine(0)[0];
            Assert.Equal(10, a.Attributes.Foreground.Index);
        }
    }

    [Fact]
    public void Sgr_TrueColorForeground()
    {
        var e = NewEngine();
        Feed(e, ESC + "[38;2;10;20;30mA");
        lock (e.Buffer.SyncRoot)
        {
            var a = e.Buffer.GetLine(0)[0];
            Assert.Equal(ColorKind.Rgb, a.Attributes.Foreground.Kind);
            Assert.Equal(10, a.Attributes.Foreground.R);
            Assert.Equal(20, a.Attributes.Foreground.G);
            Assert.Equal(30, a.Attributes.Foreground.B);
        }
    }

    [Fact]
    public void UnsupportedSequences_DoNotLeakToScreen()
    {
        var e = NewEngine();
        Feed(e, ESC + "[?25lhi");    // hide cursor (private mode)
        Feed(e, ESC + "[10;5Hx");    // CUP (ignored) + x
        var text = LineText(e, 0);
        // 미지원 시퀀스(?25l 사설 모드, CUP)는 완전히 소비되고 인쇄 문자만 남아야 함
        Assert.Equal("hix", text);
        Assert.False(text.Contains('')); // ESC(제어 문자)가 버퍼에 새지 않음 (Ordinal)
    }

    [Fact]
    public void EraseToEnd_TrimsFromCursor()
    {
        var e = NewEngine();
        Feed(e, "abcdef");        // cursor at 6
        Feed(e, ESC + "[3D");     // CUB 3 -> cursor 3
        Feed(e, ESC + "[K");      // erase to end
        Assert.Equal("abc", LineText(e, 0));
    }

    [Fact]
    public void CursorForward_PadsWithSpaces()
    {
        var e = NewEngine();
        Feed(e, "A" + ESC + "[3CB"); // A(col0->1), CUF 3 -> col4, B at col4
        Assert.Equal("A   B", LineText(e, 0));
    }

    [Fact]
    public void Utf8_MultibyteSplitAcrossChunks_DecodesCorrectly()
    {
        var e = NewEngine();
        byte[] ga = Encoding.UTF8.GetBytes("가"); // EA B0 80
        Assert.Equal(3, ga.Length);
        e.Receive(ga.AsSpan(0, 2)); // 첫 2바이트만
        e.Receive(ga.AsSpan(2, 1)); // 마지막 1바이트
        Assert.Equal("가", LineText(e, 0));
    }

    [Fact]
    public void Utf8_KoreanTextAndWidth()
    {
        var e = NewEngine();
        Feed(e, "한글 test");
        Assert.Equal("한글 test", LineText(e, 0));
        lock (e.Buffer.SyncRoot)
        {
            // 한(2) 글(2) 공백(1) t e s t(4) = 9
            Assert.Equal(9, e.Buffer.GetLine(0).DisplayWidth);
        }
    }

    [Fact]
    public void EspIdfStyleLogLine_StripsEscapesKeepsColor()
    {
        var e = NewEngine();
        // ESP-IDF: 초록색 Info 로그
        Feed(e, ESC + "[0;32mI (1234) wifi: connected" + ESC + "[0m\n");
        Assert.Equal("I (1234) wifi: connected", LineText(e, 0));
        lock (e.Buffer.SyncRoot)
        {
            var cell = e.Buffer.GetLine(0)[0]; // 'I'
            Assert.Equal(2, cell.Attributes.Foreground.Index); // green
        }
    }

    [Fact]
    public void Dsr_CursorPositionReport_IsSent()
    {
        var e = NewEngine();
        byte[]? response = null;
        e.Respond = mem => response = mem.ToArray();
        Feed(e, "abc");            // cursor col 3 -> report col 4
        Feed(e, ESC + "[6n");      // DSR
        Assert.NotNull(response);
        string s = Encoding.ASCII.GetString(response!);
        Assert.Equal(ESC + "[1;4R", s);
    }

    [Fact]
    public void Dsr_DeviceStatus_RespondsOk()
    {
        // linenoise 프로브: ESC[5n → 터미널은 ESC[0n("정상")으로 응답해야 이스케이프 지원이 감지됨(§6 Q2)
        var e = NewEngine();
        byte[]? response = null;
        e.Respond = mem => response = mem.ToArray();
        Feed(e, ESC + "[5n");
        Assert.NotNull(response);
        Assert.Equal(ESC + "[0n", Encoding.ASCII.GetString(response!));
    }

    [Fact]
    public void Scrollback_TrimsOldestBeyondCap()
    {
        var e = new TerminalEngine(new UTF8Encoding(false), maxLines: 100);
        for (int i = 0; i < 250; i++)
            Feed(e, $"line{i}\n");
        lock (e.Buffer.SyncRoot)
        {
            Assert.True(e.Buffer.LineCount <= 100);
            Assert.True(e.Buffer.TrimmedCount >= 150);
        }
    }
}
