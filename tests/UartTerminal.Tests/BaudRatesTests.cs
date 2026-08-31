using UartTerminal.Core.Serial;
using Xunit;

namespace UartTerminal.Tests;

/// <summary>
/// 속도는 이제 목록에서 고르는 것만이 아니라 <b>직접 칠 수도 있다.</b>
/// 잘못 읽으면 엉뚱한 속도로 접속해 글자가 깨지고, 원인이 화면에 안 보인다 —
/// "왜 깨지지" 를 한참 헤매게 되는 종류라 파싱을 못으로 박아 둔다.
/// </summary>
public class BaudRatesTests
{
    [Theory]
    [InlineData("115200", 115200)]
    [InlineData("9600", 9600)]
    [InlineData("  115200  ", 115200)]   // 앞뒤 공백
    [InlineData("115,200", 115200)]      // 목록에서 복사해 붙이는 경로
    [InlineData("1_000_000", 1000000)]
    [InlineData("2000000", 2000000)]     // 목록 밖 고속도 받아야 한다
    public void TryParse_AcceptsUsableValues(string text, int expected)
    {
        Assert.True(BaudRates.TryParse(text, out int baud));
        Assert.Equal(expected, baud);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]          // SerialPort 가 예외를 던진다
    [InlineData("-9600")]
    [InlineData("115200bps")]  // 단위를 같이 친 경우
    [InlineData("십일만")]
    [InlineData("11.5200")]
    public void TryParse_RejectsUnusableValues(string? text)
    {
        Assert.False(BaudRates.TryParse(text, out int baud));
        Assert.Equal(0, baud);
    }

    /// <summary>
    /// 목록은 오름차순이어야 한다 — 콤보에 그대로 들어가므로 순서가 어긋나면 화면에서 바로 보인다.
    /// </summary>
    [Fact]
    public void Common_IsSortedAndDistinct()
    {
        Assert.Equal(BaudRates.Common.OrderBy(v => v).ToArray(), BaudRates.Common);
        Assert.Equal(BaudRates.Common.Distinct().Count(), BaudRates.Common.Length);
        Assert.All(BaudRates.Common, v => Assert.True(v > 0));
    }

    /// <summary>
    /// 74880 은 ESP32 ROM 부트로더의 출력 속도다. 표준 목록에는 없어서 "정리" 하다 지우기 쉬운데,
    /// 없으면 부트루프를 진단할 때 글자가 깨져 보인다.
    /// </summary>
    [Fact]
    public void Common_KeepsBootloaderRate()
    {
        Assert.Contains(74880, BaudRates.Common);
        Assert.Contains(BaudRates.Default, BaudRates.Common);
    }
}
