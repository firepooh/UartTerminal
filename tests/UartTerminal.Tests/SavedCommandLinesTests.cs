using System.Text.Json;
using UartTerminal.Core.Config;
using Xunit;

namespace UartTerminal.Tests;

/// <summary>
/// 저장 명령이 <b>여러 줄</b>을 받게 되면서 생긴 규칙(README §8 — 원래 비목표였다).
/// 줄을 잘못 나누면 엉뚱한 문자열이 보드로 나가는데, 화면에는 보낸 대로 찍혀서
/// "왜 안 먹지" 를 한참 헤매게 된다. 그래서 나누는 규칙을 못으로 박아 둔다.
/// </summary>
public class SavedCommandLinesTests
{
    [Fact]
    public void SingleLine_StaysOneLine()
    {
        var cmd = new SavedCommand { Text = "ver" };

        Assert.Equal(new[] { "ver" }, cmd.Lines);
        Assert.False(cmd.IsMultiLine);
    }

    [Theory]
    [InlineData("a\nb")]        // LF
    [InlineData("a\r\nb")]      // CRLF
    [InlineData("a\rb")]        // CR
    public void SplitsOnEveryNewlineFlavour(string text)
    {
        Assert.Equal(new[] { "a", "b" }, SavedCommand.SplitLines(text));
    }

    /// <summary>입력칸 끝에 남는 개행이 빈 전송이 되면 안 된다.</summary>
    [Fact]
    public void DropsBlankLines()
    {
        var cmd = new SavedCommand { Text = "ver\n\n  \nfree\n" };

        Assert.Equal(new[] { "ver", "free" }, cmd.Lines);
        Assert.True(cmd.IsMultiLine);
    }

    /// <summary>붙여넣기로 섞여 들어오는 들여쓰기·꼬리 공백은 콘솔 명령에서 의미가 없다.</summary>
    [Fact]
    public void TrimsEachLine()
    {
        Assert.Equal(new[] { "ver", "free" }, SavedCommand.SplitLines("  ver  \n\tfree\t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void EmptyTextYieldsNoLines(string? text)
    {
        Assert.Empty(SavedCommand.SplitLines(text));
    }

    // ── 지연 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Delay_DefaultsTo500WhenUnset()
    {
        Assert.Equal(500, SavedCommand.DefaultDelayMs);
        Assert.Equal(500, new SavedCommand { Text = "a\nb" }.EffectiveDelayMs);
    }

    [Fact]
    public void Delay_UsesGivenValue()
    {
        Assert.Equal(0, new SavedCommand { DelayMs = 0 }.EffectiveDelayMs);
        Assert.Equal(1500, new SavedCommand { DelayMs = 1500 }.EffectiveDelayMs);
    }

    /// <summary>손편집으로 음수가 들어와도 Task.Delay 가 터지지 않아야 한다.</summary>
    [Fact]
    public void Delay_NegativeIsTreatedAsZero()
    {
        Assert.Equal(0, new SavedCommand { DelayMs = -1 }.EffectiveDelayMs);
    }

    // ── 파일 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 한 줄짜리(대부분)에 쓸모없는 필드가 붙지 않아야 한다 — 그래서 DelayMs 는 nullable 이다.
    /// 500 을 int 기본값으로 뒀다면 파일마다 delayMs 가 따라붙는다.
    /// </summary>
    [Fact]
    public void Json_OmitsDelayWhenUnset()
    {
        string json = JsonSerializer.Serialize(new SavedCommand { Name = "ver", Text = "ver" });

        Assert.DoesNotContain("delayMs", json);
    }

    [Fact]
    public void Json_RoundTripsDelay()
    {
        var src = new SavedCommand { Name = "boot", Text = "reset\nver", DelayMs = 1200 };

        var back = JsonSerializer.Deserialize<SavedCommand>(JsonSerializer.Serialize(src))!;

        Assert.Equal(1200, back.DelayMs);
        Assert.Equal(new[] { "reset", "ver" }, back.Lines);
    }

    /// <summary>delayMs 를 모르는 옛 파일도 그대로 읽혀야 한다.</summary>
    [Fact]
    public void Json_OldFileWithoutDelayLoadsWithDefault()
    {
        var back = JsonSerializer.Deserialize<SavedCommand>("""{"name":"ver","text":"ver"}""")!;

        Assert.Null(back.DelayMs);
        Assert.Equal(500, back.EffectiveDelayMs);
    }
}
