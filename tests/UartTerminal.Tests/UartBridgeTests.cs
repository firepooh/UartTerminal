using System.Text;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;
using UartTerminal.Mcp;

namespace UartTerminal.Tests;

/// <summary>
/// MCP 세션 공유 파사드(UartBridge)의 수명주기·접근제어·포트제어 위임 테스트.
/// 실제 시리얼 포트 없이 <see cref="FakeSerialSession"/> 으로 attach/detach·수신·송신을 구동한다.
/// </summary>
public class UartBridgeTests
{
    private static UartBridge NewBridge() => new(new TerminalEngine(new UTF8Encoding(false)));

    private static (UartBridge bridge, FakeSerialSession fake) Connected(bool enabled = false)
    {
        var b = NewBridge();
        var fake = new FakeSerialSession();
        fake.Open();
        b.AttachSession(fake);
        b.Enabled = enabled;
        return (b, fake);
    }

    // ── 상태/수명주기 ────────────────────────────────────────────────────────

    [Fact]
    public void Status_Disconnected_WhenNoSession()
    {
        Assert.False(NewBridge().Status().Connected);
    }

    [Fact]
    public void Status_ReflectsAttachedSession()
    {
        var b = NewBridge();
        var fake = new FakeSerialSession("COM7", 230400);
        fake.Open();
        b.AttachSession(fake);

        var s = b.Status();
        Assert.True(s.Connected);
        Assert.Equal("COM7", s.Port);
        Assert.Equal(230400, s.Baud);
    }

    [Fact]
    public void Detach_MarksDisconnected()
    {
        var (b, _) = Connected();
        Assert.True(b.Status().Connected);
        b.DetachSession();
        Assert.False(b.Status().Connected);
    }

    [Fact]
    public void Reattach_ClearsRingBuffer()
    {
        var b = NewBridge();
        var a = new FakeSerialSession(); a.Open(); b.AttachSession(a);
        a.EmitData(Encoding.ASCII.GetBytes("old"));

        var c = new FakeSerialSession(); c.Open(); b.AttachSession(c); // 재연결 시 링버퍼 초기화
        var r = b.Read(cursor: null, maxBytes: 100, stripAnsi: true);
        Assert.Equal("", r.Data);
    }

    // ── 송신 접근제어 ────────────────────────────────────────────────────────

    [Fact]
    public void Send_Fails_WhenDisabled()
    {
        var r = NewBridge().Send("hi", true);
        Assert.False(r.Ok);
        Assert.Equal("mcp_disabled", r.Error);
    }

    [Fact]
    public void Send_Fails_WhenReadOnly()
    {
        var (b, _) = Connected(enabled: true);
        b.ReadOnly = true;
        var r = b.Send("hi", true);
        Assert.False(r.Ok);
        Assert.Equal("read_only", r.Error);
    }

    [Fact]
    public void Send_Fails_WhenDisconnected()
    {
        var b = NewBridge();
        b.Enabled = true;
        var r = b.Send("hi", true);
        Assert.False(r.Ok);
        Assert.Equal("disconnected", r.Error);
    }

    [Fact]
    public void Send_Enqueues_WhenEnabledAndConnected()
    {
        var (b, fake) = Connected(enabled: true);
        var r = b.Send("abc", appendNewline: false);

        Assert.True(r.Ok);
        Assert.Equal(3, r.BytesSent);
        Assert.Single(fake.Sent);
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, fake.Sent[0]);
    }

    [Fact]
    public void Send_AppendsNewline_AsCr()
    {
        var (b, fake) = Connected(enabled: true);
        b.Send("x", appendNewline: true);
        Assert.Equal(new byte[] { (byte)'x', 0x0D }, fake.Sent[0]);
    }

    [Fact]
    public void Send_NormalizesLfToCr()
    {
        var (b, fake) = Connected(enabled: true);
        b.Send("a\nb", appendNewline: false);
        Assert.Equal(new byte[] { (byte)'a', 0x0D, (byte)'b' }, fake.Sent[0]);
    }

    // ── 수신 커서 ────────────────────────────────────────────────────────────

    [Fact]
    public void Read_ReturnsReceivedData()
    {
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("hello"));
        var r = b.Read(null, 100, true);
        Assert.Equal("hello", r.Data);
        Assert.True(r.Connected);
    }

    [Fact]
    public void Read_CursorAdvances_ReadsOnlyNewData()
    {
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("abc"));
        var r1 = b.Read(null, 100, true);
        fake.EmitData(Encoding.ASCII.GetBytes("def"));
        var r2 = b.Read(r1.Cursor, 100, true);
        Assert.Equal("def", r2.Data);
    }

    // ── 제어선(DTR/RTS) 게이팅 ───────────────────────────────────────────────

    [Fact]
    public void SetDtrRts_Gating_ThenApplies()
    {
        var b = NewBridge();
        Assert.Equal("mcp_disabled", b.SetDtrRts(true, true).Error);

        b.Enabled = true;
        Assert.Equal("disconnected", b.SetDtrRts(true, true).Error);

        var fake = new FakeSerialSession(); fake.Open(); b.AttachSession(fake);
        b.ReadOnly = true;
        Assert.Equal("read_only", b.SetDtrRts(true, true).Error);

        b.ReadOnly = false;
        var ok = b.SetDtrRts(dtr: true, rts: false);
        Assert.True(ok.Ok);
        Assert.True(fake.DtrEnabled);
        Assert.False(fake.RtsEnabled);
    }

    // ── 포트 제어(uart_close/uart_open) 위임 ─────────────────────────────────

    [Fact]
    public async Task ClosePort_Gating()
    {
        var b = NewBridge();
        Assert.Equal("mcp_disabled", (await b.ClosePortAsync()).Error);

        b.Enabled = true;
        b.ReadOnly = true;
        Assert.Equal("read_only", (await b.ClosePortAsync()).Error);

        b.ReadOnly = false;
        Assert.Equal("not_supported", (await b.ClosePortAsync()).Error); // 핸들러 미등록
    }

    [Fact]
    public async Task PortController_IsInvoked()
    {
        var b = NewBridge();
        b.Enabled = true;
        bool closeCalled = false, openCalled = false;
        b.SetPortController(
            () => { closeCalled = true; return Task.FromResult(new PortActionResult { Ok = true, State = "closed" }); },
            () => { openCalled = true; return Task.FromResult(new PortActionResult { Ok = true, State = "open" }); });

        var rc = await b.ClosePortAsync();
        Assert.True(rc.Ok);
        Assert.Equal("closed", rc.State);
        Assert.True(closeCalled);

        var ro = await b.OpenPortAsync();
        Assert.True(ro.Ok);
        Assert.Equal("open", ro.State);
        Assert.True(openCalled);
    }

    [Fact]
    public async Task PortController_Exception_IsNormalized()
    {
        var b = NewBridge();
        b.Enabled = true;
        b.SetPortController(
            () => throw new InvalidOperationException("boom"),
            () => Task.FromResult(new PortActionResult { Ok = true }));

        var r = await b.ClosePortAsync();
        Assert.False(r.Ok);
        Assert.StartsWith("exception:", r.Error);
    }

    // ── expect(패턴 대기) ────────────────────────────────────────────────────

    [Fact]
    public async Task Expect_MatchesAlreadyBufferedData()
    {
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("boot: app_main started"));
        var r = await b.ExpectAsync("app_main", timeoutMs: 500, cursor: 0, stripAnsi: true, useRegex: false, ct: default);
        Assert.True(r.Matched);
        Assert.Equal("app_main", r.Match);
    }

    [Fact]
    public async Task Expect_TimesOut_WhenNoMatch()
    {
        var (b, _) = Connected();
        var r = await b.ExpectAsync("never", timeoutMs: 80, cursor: 0, stripAnsi: true, useRegex: false, ct: default);
        Assert.False(r.Matched);
        Assert.True(r.TimedOut);
    }
}
