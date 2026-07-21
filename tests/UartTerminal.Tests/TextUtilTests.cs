using System.Text;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

public class Utf8BoundaryTests
{
    [Fact]
    public void AllAscii_FullLength()
    {
        var b = Encoding.UTF8.GetBytes("hello");
        Assert.Equal(b.Length, Utf8Boundary.CompleteLength(b));
    }

    [Fact]
    public void CompleteMultibyte_FullLength()
    {
        var b = Encoding.UTF8.GetBytes("가나"); // 6 bytes, complete
        Assert.Equal(6, Utf8Boundary.CompleteLength(b));
    }

    [Fact]
    public void TruncatedMultibyte_TrimsPartialTail()
    {
        var full = Encoding.UTF8.GetBytes("ab가"); // a,b + 3바이트(가)
        Assert.Equal(5, full.Length);
        // 앞 4바이트(a,b + 가의 선행 2바이트)에서 완전한 부분은 'ab' = 2바이트
        var truncated = full.AsSpan(0, 4).ToArray();
        Assert.Equal(2, Utf8Boundary.CompleteLength(truncated));
    }

    [Fact]
    public void LeadByteOnlyAtEnd_TrimmedOff()
    {
        var full = Encoding.UTF8.GetBytes("x가");
        // 'x'(1) + 가의 선행 바이트(1)만 = 2바이트 → 완전은 'x'까지 = 1
        var partial = full.AsSpan(0, 2).ToArray();
        Assert.Equal(1, Utf8Boundary.CompleteLength(partial));
    }

    [Fact]
    public void Empty_Zero()
    {
        Assert.Equal(0, Utf8Boundary.CompleteLength(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RoundTrip_SplitDecodePreservesKorean()
    {
        // 링버퍼 읽기처럼 조각을 완전 경계까지만 소비하며 이어붙이면 원문이 복원되어야 한다.
        var src = Encoding.UTF8.GetBytes("한글 테스트 로그 12345");
        var sb = new StringBuilder();
        int pos = 0;
        int step = 4; // 일부러 문자 경계와 어긋나게 자름
        while (pos < src.Length)
        {
            int take = Math.Min(step, src.Length - pos);
            var chunk = src.AsSpan(pos, take);
            int complete = Utf8Boundary.CompleteLength(chunk);
            if (complete == 0) { step += 1; continue; } // 더 확보 필요
            sb.Append(Encoding.UTF8.GetString(chunk[..complete]));
            pos += complete;
            step = 4;
        }
        Assert.Equal("한글 테스트 로그 12345", sb.ToString());
    }
}

public class AnsiTextTests
{
    private const string ESC = "\x1b";

    [Fact]
    public void StripsSgrColor_KeepsText()
    {
        Assert.Equal("A", AnsiText.Strip(ESC + "[32mA" + ESC + "[0m"));
    }

    [Fact]
    public void EspIdfLine_BecomesPlain()
    {
        string line = ESC + "[0;32mI (1234) wifi: connected" + ESC + "[0m";
        Assert.Equal("I (1234) wifi: connected", AnsiText.Strip(line));
    }

    [Fact]
    public void RemovesCursorSequences()
    {
        Assert.Equal("hix", AnsiText.Strip(ESC + "[?25lhi" + ESC + "[10;5Hx"));
    }

    [Fact]
    public void RemovesOscTitle()
    {
        // OSC 0 ; title <BEL> done  — BEL 이 OSC 종료자
        Assert.Equal("done", AnsiText.Strip(ESC + "]0;my title\x07" + "done"));
    }

    [Fact]
    public void NormalizesNewlines()
    {
        Assert.Equal("a\nb", AnsiText.Strip("a\r\nb"));
        Assert.Equal("ab", AnsiText.Strip("a\rb")); // 단독 CR 제거
    }

    [Fact]
    public void KeepsTab_DropsOtherControls()
    {
        Assert.Equal("a\tb", AnsiText.Strip("a\tb\x07")); // BEL 제거, 탭 유지
    }

    [Fact]
    public void PreservesKorean()
    {
        Assert.Equal("한글 로그", AnsiText.Strip(ESC + "[33m한글 로그" + ESC + "[0m"));
    }
}
