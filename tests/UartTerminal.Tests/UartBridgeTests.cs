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

    /// <summary>
    /// <see cref="UartBridge.AttachSession"/> 은 링버퍼를 <b>보존</b>한다.
    /// 예전에는 여기서 지웠는데, ConnectionController 가 <c>session.Open()</c> <i>전에</i> Attach 하므로
    /// <b>실패한 오픈도 버퍼를 비웠다</b> — 자동 재연결이 1.5초마다 재시도하니 장치 분리 직전의
    /// 패닉 로그가 첫 재시도에서 사라졌다. 초기화는 오픈 성공 후 <see cref="UartBridge.ResetRing"/> 가 한다.
    /// </summary>
    [Fact]
    public void Attach_PreservesRingBuffer_SoFailedOpenCannotEraseLog()
    {
        var b = NewBridge();
        var a = new FakeSerialSession(); a.Open(); b.AttachSession(a);
        a.EmitData(Encoding.ASCII.GetBytes("panic: StoreProhibited"));

        var c = new FakeSerialSession(); c.Open(); b.AttachSession(c);

        var r = b.Read(cursor: null, maxBytes: 100, stripAnsi: true);
        Assert.Contains("panic", r.Data);
    }

    /// <summary>오픈 성공이 확정된 뒤에는 초기화된다(커서도 0).</summary>
    [Fact]
    public void ResetRing_ClearsBuffer()
    {
        var b = NewBridge();
        var a = new FakeSerialSession(); a.Open(); b.AttachSession(a);
        a.EmitData(Encoding.ASCII.GetBytes("old"));

        b.ResetRing();

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

    // ── 리셋 시퀀스(uart_reset) ──────────────────────────────────────────────

    [Fact]
    public async Task Reset_Gating()
    {
        var b = NewBridge();
        Assert.Equal("mcp_disabled", (await b.ResetAsync(bootloader: false)).Error);

        b.Enabled = true;
        Assert.Equal("disconnected", (await b.ResetAsync(bootloader: false)).Error);

        var fake = new FakeSerialSession(); fake.Open(); b.AttachSession(fake);
        b.ReadOnly = true;
        Assert.Equal("read_only", (await b.ResetAsync(bootloader: false)).Error);
        Assert.Empty(fake.ControlLines); // 차단됐으면 제어선을 건드리지 않는다
    }

    [Fact]
    public async Task Reset_Hard_RunsEnPulse()
    {
        var (b, fake) = Connected(enabled: true);
        var r = await b.ResetAsync(bootloader: false);

        Assert.True(r.Ok);
        Assert.Equal("hard", r.Mode);
        Assert.Equal(new[] { (false, true), (false, false) }, fake.ControlLines);
    }

    [Fact]
    public async Task Reset_Bootloader_PullsIo0Low()
    {
        var (b, fake) = Connected(enabled: true);
        var r = await b.ResetAsync(bootloader: true);

        Assert.True(r.Ok);
        Assert.Equal("bootloader", r.Mode);
        Assert.Equal(new[] { (false, true), (true, false), (false, false) }, fake.ControlLines);
    }

    // ── AI 송신 개행이 사용자 설정을 따르는지 ────────────────────────────────

    [Fact]
    public void Send_UsesConfiguredTransmitNewline()
    {
        var (b, fake) = Connected(enabled: true);
        b.TransmitNewline = TransmitNewline.CrLf;
        b.Send("x", appendNewline: true);
        Assert.Equal(new byte[] { (byte)'x', 0x0D, 0x0A }, fake.Sent[0]);
    }

    [Fact]
    public void Send_LfMode_NormalizesCrLfToLf()
    {
        var (b, fake) = Connected(enabled: true);
        b.TransmitNewline = TransmitNewline.Lf;
        b.Send("a\r\nb", appendNewline: false);
        Assert.Equal(new byte[] { (byte)'a', 0x0A, (byte)'b' }, fake.Sent[0]);
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

    // ── 조기 매칭(부분 도착) — 사용자 보고 회귀 ───────────────────────────────
    // 수신은 스트림이라 청크가 도착할 때마다 평가된다. 값이 다 오기 전에 열린 수량자가 걸리는 함정.

    [Fact]
    public async Task Expect_StreamMode_MatchesPartialToken_ByDesign()
    {
        // 문서화된 기본 동작: "CDC" 의 'C' 만 도착해도 \w+ 가 성립한다.
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("mode : C"));
        var r = await b.ExpectAsync(@"mode\s+:\s+\w+", timeoutMs: 200, cursor: 0,
            stripAnsi: true, useRegex: true, ct: default);

        Assert.True(r.Matched);
        Assert.Equal("mode : C", r.Match); // 조기 매칭 — line 모드가 필요한 이유
    }

    [Fact]
    public async Task Expect_LineMode_WaitsForCompleteLine()
    {
        // 같은 패턴이라도 줄이 완성된 뒤 평가하므로 값 전체("CDC")를 얻는다.
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("mode : C"));

        var task = b.ExpectAsync(@"mode\s+:\s+\w+", timeoutMs: 3000, cursor: 0,
            stripAnsi: true, useRegex: true, ct: default, lineMode: true);

        await Task.Delay(120);
        Assert.False(task.IsCompleted); // 아직 줄이 안 끝났으므로 매칭하지 않는다

        fake.EmitData(Encoding.ASCII.GetBytes("DC\r\n"));
        var r = await task;

        Assert.True(r.Matched);
        Assert.Equal("mode : CDC", r.Match);
    }

    [Fact]
    public async Task Expect_LineMode_TimesOutOnUnterminatedPrompt()
    {
        // line 모드의 대가: 개행이 없는 프롬프트는 완성되지 않아 매칭되지 않는다.
        // (그래서 기본값이 아니라 옵션이며, 툴 설명이 이 점을 안내한다.)
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("xcp> "));
        var r = await b.ExpectAsync("xcp> ", timeoutMs: 120, cursor: 0,
            stripAnsi: true, useRegex: false, ct: default, lineMode: true);

        Assert.False(r.Matched);
        Assert.True(r.TimedOut);
    }

    // ── 청크 경계에 걸린 ANSI 이스케이프 ──────────────────────────────────────

    [Fact]
    public async Task Read_SplitEscapeSequence_DoesNotLeakLiteralTail()
    {
        // ESC[3 | 2m 으로 갈리면, 무상태 strip 은 뒤 조각을 리터럴 "2m" 으로 흘린다.
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("A\x1b[3"));   // 시퀀스 중간에서 끊김

        var first = b.Read(cursor: 0, maxBytes: 4096, stripAnsi: true);
        Assert.Equal("A", first.Data);                        // 미완성 시퀀스는 보류

        fake.EmitData(Encoding.ASCII.GetBytes("2mB"));        // 나머지 도착
        var second = b.Read(first.Cursor, maxBytes: 4096, stripAnsi: true);

        Assert.Equal("B", second.Data);                       // "2mB" 가 아니어야 한다
        Assert.DoesNotContain("2m", first.Data + second.Data);
    }

    [Fact]
    public async Task Expect_SplitEscapeSequence_DoesNotCauseFalseMatch()
    {
        var (b, fake) = Connected();
        fake.EmitData(Encoding.ASCII.GetBytes("I (100) app: \x1b[0;3"));

        // 누출되면 "2m" 이 텍스트에 섞여 이 패턴이 잘못 걸린다.
        var r = await b.ExpectAsync(@"\d+m", timeoutMs: 150, cursor: 0,
            stripAnsi: true, useRegex: true, ct: default);

        Assert.False(r.Matched);
        Assert.DoesNotContain("2m", r.Data);
    }
}
