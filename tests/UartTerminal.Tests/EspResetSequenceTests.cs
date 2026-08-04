using UartTerminal.Core.Serial;

namespace UartTerminal.Tests;

/// <summary>
/// ESP32 리셋/부트로더 시퀀스 회귀 테스트.
/// 극성이 뒤바뀌면(assert=LOW 규칙을 잊으면) 보드가 리셋되지 않거나 엉뚱하게 부트로더로 들어가므로
/// 단계 순서와 값을 그대로 고정한다. 진리표: Enable(false,true)=EN L / Enable(true,false)=IO0 L.
/// </summary>
public class EspResetSequenceTests
{
    private static FakeSerialSession OpenFake()
    {
        var s = new FakeSerialSession();
        s.Open();
        return s;
    }

    [Fact]
    public void HardReset_PulsesEnOnly_Io0StaysHigh()
    {
        var s = OpenFake();
        EspResetSequence.Apply(s, EspResetSequence.HardReset, sleep: _ => { });

        Assert.Equal(new[] { (false, true), (false, false) }, s.ControlLines);
        // DTR 은 한 번도 assert 되지 않는다 → IO0 는 계속 HIGH → 평소처럼 펌웨어 부팅.
        Assert.All(s.ControlLines, step => Assert.False(step.Dtr));
    }

    [Fact]
    public void Bootloader_ReleasesResetWhileIo0Low()
    {
        var s = OpenFake();
        EspResetSequence.Apply(s, EspResetSequence.Bootloader, sleep: _ => { });

        Assert.Equal(new[] { (false, true), (true, false), (false, false) }, s.ControlLines);
    }

    [Fact]
    public void HardReset_LeavesBothLinesDeasserted()
    {
        var s = OpenFake();
        EspResetSequence.Apply(s, EspResetSequence.HardReset, sleep: _ => { });

        // 시퀀스 후 기본(오픈 시) 상태와 같아야 한다 — 보드를 계속 붙잡고 있으면 안 된다.
        Assert.False(s.DtrEnabled);
        Assert.False(s.RtsEnabled);
    }

    [Fact]
    public void Delays_MatchEsptoolTiming()
    {
        var slept = new List<int>();
        var s = OpenFake();
        EspResetSequence.Apply(s, EspResetSequence.HardReset, sleep: slept.Add);

        Assert.Equal(new[] { EspResetSequence.AssertMs, EspResetSequence.SettleMs }, slept);
        Assert.Equal(100, EspResetSequence.AssertMs);
        Assert.Equal(50, EspResetSequence.SettleMs);
    }

    [Fact]
    public void ClosedSession_NoControlLineWrites()
    {
        var s = new FakeSerialSession(); // 열지 않음
        EspResetSequence.Apply(s, EspResetSequence.HardReset, sleep: _ => { });
        Assert.Empty(s.ControlLines);
    }

    [Fact]
    public void SessionClosedMidSequence_StopsEarly()
    {
        var s = OpenFake();
        // 첫 단계 대기 중에 세션이 닫히면(케이블 분리 등) 남은 단계를 쓰지 않는다.
        EspResetSequence.Apply(s, EspResetSequence.Bootloader, sleep: _ => s.Dispose());

        Assert.Single(s.ControlLines);
        Assert.Equal((false, true), s.ControlLines[0]);
    }

    [Fact]
    public async Task ApplyAsync_SameOrderAsSync()
    {
        var s = OpenFake();
        await EspResetSequence.ApplyAsync(s, EspResetSequence.Bootloader);

        Assert.Equal(new[] { (false, true), (true, false), (false, false) }, s.ControlLines);
    }

    [Fact]
    public void ResetOnOpen_IsOptOut_ByDefault()
    {
        Assert.False(SerialConnectionParams.Default.ResetOnOpen);
        Assert.False(SerialConnectionParams.Default.DtrEnable);
        Assert.False(SerialConnectionParams.Default.RtsEnable);
    }
}
