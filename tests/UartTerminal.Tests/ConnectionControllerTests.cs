using System.Text;
using UartTerminal;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;
using UartTerminal.Mcp;

namespace UartTerminal.Tests;

/// <summary>
/// 연결 수명주기 상태머신(ConnectionController) 회귀 테스트.
/// 이번 프로젝트의 자동 재연결 리뷰에서 나온 확정 결함 9건(지연 콜백 재무장, 전역 토글, MCP 양보 등)을
/// 가짜 세션 + 수동 이벤트/틱으로 결정론적으로 재현해 고정한다.
/// </summary>
public class ConnectionControllerTests
{
    /// <summary>가짜 세션 팩토리 + 동기 post 로 컨트롤러를 구동하는 테스트 하네스.</summary>
    private sealed class Harness
    {
        public readonly List<FakeSerialSession> Created = new();
        public FakeOpenMode NextMode = FakeOpenMode.Success;
        public bool AutoReconnect = true;
        public string Port = "COM8";
        public readonly ConnectionController Ctl;

        public Harness()
        {
            var engine = new TerminalEngine(new UTF8Encoding(false));
            var bridge = new UartBridge(engine);
            Ctl = new ConnectionController(
                engine, bridge,
                factory: () => { var f = new FakeSerialSession(Port) { OpenMode = NextMode }; Created.Add(f); return f; },
                portName: () => Port,
                post: a => a(),                 // 동기 실행(테스트에서 즉시 처리)
                autoReconnect: () => AutoReconnect,
                notify: () => { },
                status: _ => { });
        }

        public FakeSerialSession Last => Created[^1];
    }

    // ── 기본 오픈 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Open_Success_Connects()
    {
        var h = new Harness();
        Assert.Equal(OpenOutcome.Success, h.Ctl.Open());
        Assert.True(h.Ctl.IsConnected);
    }

    [Fact]
    public void Open_InUse_DoesNotConnect()
    {
        var h = new Harness { NextMode = FakeOpenMode.InUse };
        Assert.Equal(OpenOutcome.InUse, h.Ctl.Open());
        Assert.False(h.Ctl.IsConnected);
    }

    // ── 종료 사유별 판단 ─────────────────────────────────────────────────────────

    [Fact]
    public void DeviceRemoved_StartsAutoReconnect_WhenEnabled()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Last.SimulateClosed(SerialCloseReason.DeviceRemoved);
        Assert.False(h.Ctl.IsConnected);
        Assert.True(h.Ctl.IsReconnecting);
    }

    [Fact]
    public void DeviceRemoved_NoReconnect_WhenDisabled()
    {
        var h = new Harness { AutoReconnect = false };
        h.Ctl.Open();
        h.Last.SimulateClosed(SerialCloseReason.DeviceRemoved);
        Assert.False(h.Ctl.IsReconnecting);
    }

    [Fact]
    public void UserClosed_StopsReconnect()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Last.SimulateClosed(SerialCloseReason.UserClosed);
        Assert.False(h.Ctl.IsConnected);
        Assert.False(h.Ctl.IsReconnecting);
    }

    // ── 지연/낡은 Closed 콜백 무력화 (핵심 불변식) ───────────────────────────────

    // F1/F3: 탭/문서 닫힘 후 지연 도착한 DeviceRemoved 는 자동 재연결을 재무장하지 않는다(좀비 타이머 방지).
    [Fact]
    public void DelayedDeviceRemoved_AfterCloseDocument_DoesNotReArm()
    {
        var h = new Harness();
        h.Ctl.Open();
        var s = h.Last;
        h.Ctl.CloseDocument();
        s.SimulateClosed(SerialCloseReason.DeviceRemoved); // 최대 1.5s 늦게 도착하는 콜백
        Assert.False(h.Ctl.IsReconnecting);
    }

    // F7/F8: 사용자 해제 후 지연 도착한 DeviceRemoved 는 무시된다.
    [Fact]
    public void DelayedDeviceRemoved_AfterUserDisconnect_IsIgnored()
    {
        var h = new Harness();
        h.Ctl.Open();
        var s = h.Last;
        h.Ctl.Disconnect();                                // _session 즉시 null → 낡은 콜백 무시됨
        s.SimulateClosed(SerialCloseReason.DeviceRemoved);
        Assert.False(h.Ctl.IsReconnecting);
    }

    // 현재 세션이 아닌(교체된) 세션의 콜백은 상태를 건드리지 않는다.
    [Fact]
    public void StaleCallback_FromNonCurrentSession_IsIgnored()
    {
        var h = new Harness();
        h.Ctl.Open();
        var a = h.Last;
        a.SimulateClosed(SerialCloseReason.DeviceRemoved); // A 제거 → 재연결 대기
        Assert.True(h.Ctl.IsReconnecting);
        h.Ctl.ReconnectTick(portExists: true);             // B 오픈
        var b = h.Last;
        Assert.NotSame(a, b);
        Assert.True(h.Ctl.IsConnected);

        a.SimulateClosed(SerialCloseReason.DeviceRemoved); // A 의 낡은 콜백 — 무시돼야
        Assert.True(h.Ctl.IsConnected);
        Assert.False(h.Ctl.IsReconnecting);
    }

    // ── 자동 재연결 tick ─────────────────────────────────────────────────────────

    // F2/F9: 대기 중 다른 창에서 전역 자동재연결을 끄면 다음 tick 에 스스로 종료(포트를 열지 않는다).
    [Fact]
    public void ReconnectTick_Stops_WhenAutoReconnectDisabledMidWait()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Last.SimulateClosed(SerialCloseReason.DeviceRemoved);
        Assert.True(h.Ctl.IsReconnecting);

        h.AutoReconnect = false;                 // 다른 창에서 토글 OFF
        h.Ctl.ReconnectTick(portExists: true);
        Assert.False(h.Ctl.IsReconnecting);
        Assert.False(h.Ctl.IsConnected);
    }

    [Fact]
    public void ReconnectTick_KeepsWaiting_WhenPortAbsent()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Last.SimulateClosed(SerialCloseReason.DeviceRemoved);
        h.Ctl.ReconnectTick(portExists: false);
        Assert.True(h.Ctl.IsReconnecting);
    }

    [Fact]
    public void ReconnectTick_Reopens_WhenPortReturns()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Last.SimulateClosed(SerialCloseReason.DeviceRemoved);
        h.Ctl.ReconnectTick(portExists: true);
        Assert.True(h.Ctl.IsConnected);
        Assert.False(h.Ctl.IsReconnecting);
    }

    // ── MCP 포트 양보/재개 ───────────────────────────────────────────────────────

    [Fact]
    public void McpRelease_SuppressesReconnect_AndFlagsReleased()
    {
        var h = new Harness();
        h.Ctl.Open();
        var s = h.Last;

        var r = h.Ctl.McpRelease();
        Assert.True(r.Ok);
        Assert.Equal("closed", r.State);
        Assert.True(h.Ctl.IsPortReleased);
        Assert.False(h.Ctl.IsConnected);
        Assert.False(h.Ctl.IsReconnecting);

        s.SimulateClosed(SerialCloseReason.DeviceRemoved); // 양보한 세션의 지연 콜백
        Assert.False(h.Ctl.IsReconnecting);                // 재무장 금지
    }

    [Fact]
    public void McpReopen_AfterRelease_Reconnects_ClearsReleased()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Ctl.McpRelease();

        var r = h.Ctl.McpReopen();
        Assert.True(r.Ok);
        Assert.Equal("open", r.State);
        Assert.True(h.Ctl.IsConnected);
        Assert.False(h.Ctl.IsPortReleased);
    }

    [Fact]
    public void McpReopen_ReturnsInUse_WhenPortStillBusy()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Ctl.McpRelease();
        h.NextMode = FakeOpenMode.InUse;         // esptool 이 아직 점유 중
        var r = h.Ctl.McpReopen();
        Assert.False(r.Ok);
        Assert.Equal("in_use", r.State);
    }

    [Fact]
    public void UserOpen_AfterRelease_ClearsReleasedFlag()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Ctl.McpRelease();
        Assert.True(h.Ctl.IsPortReleased);

        h.Ctl.OpenUserInitiated();
        Assert.True(h.Ctl.IsConnected);
        Assert.False(h.Ctl.IsPortReleased);
    }

    // 사용자 재연결이 실패해도 'AI 양보' 상태가 남지 않는다.
    [Fact]
    public void FailedUserOpen_DoesNotLeaveReleasedFlag()
    {
        var h = new Harness();
        h.Ctl.Open();
        h.Ctl.McpRelease();
        h.NextMode = FakeOpenMode.InUse;
        h.Ctl.OpenUserInitiated();
        Assert.False(h.Ctl.IsPortReleased);
        Assert.False(h.Ctl.IsConnected);
    }
}
