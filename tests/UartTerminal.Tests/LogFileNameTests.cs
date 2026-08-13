using UartTerminal.Core.Logging;

namespace UartTerminal.Tests;

/// <summary>
/// 로그 파일 <b>기본 이름</b> 규칙(<c>세션_포트_YYMMDD_HHMMSS.log</c>) 검증.
/// 여러 포트를 동시에 로깅하는 것이 정상 사용이라, 이름만 보고 어느 보드·어느 포트·언제인지
/// 구분되지 않으면 파일이 섞인다 — 그래서 규칙 자체를 테스트로 고정한다.
/// </summary>
public sealed class LogFileNameTests
{
    private static readonly DateTime When = new(2026, 8, 13, 12, 3, 35);

    [Fact]
    public void WithSession_HasAllFourParts()
    {
        Assert.Equal("sensor_COM4_260813_120335.log", LogFileName.Default("sensor", "COM4", When));
    }

    /// <summary>세션이 없으면 앞 칸을 <b>구분자까지</b> 뺀다 — 안 그러면 "_COM4_…" 로 시작한다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutSession_DropsLeadingPartAndSeparator(string? session)
    {
        string name = LogFileName.Default(session, "COM4", When);
        Assert.Equal("COM4_260813_120335.log", name);
        Assert.False(name.StartsWith('_'));
    }

    /// <summary>세션 이름은 사람이 자유롭게 붙인다 — 공백은 '-' 로, 파일명 금지 문자는 제거.</summary>
    [Fact]
    public void SessionName_IsSanitizedForFileSystem()
    {
        Assert.Equal("pulse-simul_COM3_260813_120335.log",
            LogFileName.Default("pulse simul", "COM3", When));

        string weird = LogFileName.Default("a/b:c*d?", "COM3", When);
        Assert.Equal("abcd_COM3_260813_120335.log", weird);
        Assert.Empty(weird.Split('.')[0].Intersect(Path.GetInvalidFileNameChars()));
    }

    /// <summary>이름이 통째로 금지 문자면 세션 칸이 없는 것과 같아야 한다(빈 칸·앞 밑줄 금지).</summary>
    [Fact]
    public void SessionName_AllInvalid_BehavesAsNoSession()
    {
        Assert.Equal("COM4_260813_120335.log", LogFileName.Default("///", "COM4", When));
    }

    /// <summary>긴 이름이 경로 상한을 밀어내지 않게 자른다.</summary>
    [Fact]
    public void SessionName_IsLengthCapped()
    {
        string name = LogFileName.Default(new string('x', 200), "COM4", When);
        Assert.StartsWith(new string('x', LogFileName.MaxSessionPart) + "_COM4_", name);
    }

    /// <summary>연·월·일과 시·분·초는 두 자리로 채워야 정렬이 시간순이 된다.</summary>
    [Fact]
    public void DateAndTime_AreZeroPadded()
    {
        Assert.Equal("COM1_260105_090807.log",
            LogFileName.Default(null, "COM1", new DateTime(2026, 1, 5, 9, 8, 7)));
    }
}
