using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using UartTerminal.Core.Terminal;
using UartTerminal.Mcp;
using Xunit;

namespace UartTerminal.Tests;

/// <summary>
/// HTTP 엔드포인트가 앱당 하나가 되면서 생긴 규칙(docs/mcp-http-transport.md §3.3)의 검사.
///
/// <para>가장 중요한 것은 <see cref="Omitted_WithTwoPorts_IsAmbiguous"/> 다 —
/// 마스터·슬레이브를 같이 물린 상태에서 하나를 골라 주면 엉뚱한 보드로 명령이 나간다.
/// 애매하면 거절하는 쪽이 맞고, 그 판단이 코드에서 사라지지 않게 못을 박아 둔다.</para>
/// </summary>
public class BridgeRegistryTests
{
    private static UartBridge NewBridge() => new(new TerminalEngine(new UTF8Encoding(false)));

    // ── 해석 규칙 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Omitted_WithSinglePort_ResolvesToIt()
    {
        var reg = new BridgeRegistry();
        var com4 = NewBridge();
        reg.Register("COM4", com4);

        Assert.True(reg.TryResolve(null, out var hit, out var why));
        Assert.Same(com4, hit);
        Assert.Equal(BridgeResolution.Ok, why);
    }

    [Fact]
    public void Omitted_WithTwoPorts_IsAmbiguous()
    {
        var reg = new BridgeRegistry();
        reg.Register("COM4", NewBridge());
        reg.Register("COM7", NewBridge());

        Assert.False(reg.TryResolve(null, out var hit, out var why));
        Assert.Null(hit);                                   // 아무 데도 보내지 않는다
        Assert.Equal(BridgeResolution.Ambiguous, why);
        Assert.Equal(new[] { "COM4", "COM7" }, reg.ExposedPorts);   // 목록은 돌려준다
    }

    [Fact]
    public void Omitted_WithNoPorts_ReportsNoneExposed()
    {
        var reg = new BridgeRegistry();

        Assert.False(reg.TryResolve(null, out _, out var why));
        Assert.Equal(BridgeResolution.NoneExposed, why);
    }

    [Fact]
    public void Named_ResolvesRegardlessOfCount()
    {
        var reg = new BridgeRegistry();
        var com4 = NewBridge();
        var com7 = NewBridge();
        reg.Register("COM4", com4);
        reg.Register("COM7", com7);

        Assert.True(reg.TryResolve("COM7", out var hit, out _));
        Assert.Same(com7, hit);
        Assert.True(reg.TryResolve("COM4", out hit, out _));
        Assert.Same(com4, hit);
    }

    [Theory]
    [InlineData("com4")]
    [InlineData("COM4")]
    [InlineData("  COM4  ")]
    public void Named_IgnoresCaseAndSurroundingSpace(string spelling)
    {
        var reg = new BridgeRegistry();
        var com4 = NewBridge();
        reg.Register("COM4", com4);

        Assert.True(reg.TryResolve(spelling, out var hit, out _));
        Assert.Same(com4, hit);
    }

    [Fact]
    public void Named_Unknown_IsNotFound()
    {
        var reg = new BridgeRegistry();
        reg.Register("COM4", NewBridge());

        Assert.False(reg.TryResolve("COM9", out var hit, out var why));
        Assert.Null(hit);
        Assert.Equal(BridgeResolution.NotFound, why);
    }

    // ── 등록/해제 수명주기 ───────────────────────────────────────────────────────

    [Fact]
    public void Unregister_RemovesExposure()
    {
        var reg = new BridgeRegistry();
        var com4 = NewBridge();
        reg.Register("COM4", com4);
        Assert.True(reg.Unregister("COM4", com4));

        Assert.Equal(0, reg.Count);
        Assert.False(reg.TryResolve("COM4", out _, out var why));
        Assert.Equal(BridgeResolution.NotFound, why);
    }

    /// <summary>
    /// 닫히는 탭이 <b>같은 포트명을 이어받은 다른 탭</b>의 등록을 지우면,
    /// 화면에는 MCP 가 켜져 있는데 툴은 그 포트를 못 찾는 상태가 된다.
    /// </summary>
    [Fact]
    public void Unregister_WithStaleBridge_DoesNothing()
    {
        var reg = new BridgeRegistry();
        var old = NewBridge();
        var current = NewBridge();
        reg.Register("COM4", old);
        reg.Register("COM4", current);      // 다른 탭이 같은 포트를 이어받았다

        Assert.False(reg.Unregister("COM4", old));
        Assert.True(reg.TryResolve("COM4", out var hit, out _));
        Assert.Same(current, hit);
    }

    [Fact]
    public void ReopeningPort_DoesNotLeakRegistrations()
    {
        var reg = new BridgeRegistry();
        var bridge = NewBridge();

        for (int i = 0; i < 5; i++)
        {
            reg.Register("COM4", bridge);
            Assert.True(reg.Unregister("COM4", bridge));
        }

        Assert.Equal(0, reg.Count);
        Assert.Empty(reg.ExposedPorts);
    }

    /// <summary>0↔1 을 넘을 때만 호스트를 켜고 끈다 — 그 판단의 입력이 이 이벤트다.</summary>
    [Fact]
    public void ExposedCountChanged_FiresWithNewCount()
    {
        var reg = new BridgeRegistry();
        var a = NewBridge();
        var b = NewBridge();
        var seen = new List<int>();
        reg.ExposedCountChanged += seen.Add;

        reg.Register("COM4", a);
        reg.Register("COM7", b);
        reg.Register("COM7", b);            // 같은 것을 또 넣어도 신호는 없다
        reg.Unregister("COM4", a);
        reg.Unregister("COM4", a);          // 없는 것을 빼도 신호는 없다
        reg.Unregister("COM7", b);

        Assert.Equal(new[] { 1, 2, 1, 0 }, seen);
    }
}

/// <summary>
/// 툴 표면의 규약. 엔드포인트가 앱당 하나이므로 <b>모든</b> 툴이 대상을 지정할 수 있어야 한다 —
/// 하나라도 빠지면 그 툴만 포트를 못 고르고, 포트가 둘일 때 조용히 쓸 수 없게 된다.
/// </summary>
public class UartMcpToolSurfaceTests
{
    private static IEnumerable<MethodInfo> Tools() =>
        typeof(UartMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Fact]
    public void AllNineToolsAreStillThere()
    {
        var names = Tools()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "uart_close", "uart_expect", "uart_open", "uart_read", "uart_reset",
                "uart_screen", "uart_send", "uart_set_dtr_rts", "uart_status",
            },
            names);
    }

    [Fact]
    public void EveryToolTakesAnOptionalPort()
    {
        foreach (var m in Tools())
        {
            string name = m.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            var port = Array.Find(m.GetParameters(), p => p.Name == "port");

            Assert.True(port is not null, $"{name} 에 port 인자가 없다");
            Assert.Equal(typeof(string), port!.ParameterType);
            Assert.True(port.IsOptional, $"{name} 의 port 가 선택 인자가 아니다");
            Assert.Null(port.DefaultValue);
        }
    }

    /// <summary>
    /// 기존 사용법 호환(§3.4): port 는 <b>마지막</b> 선택 인자여야 한다.
    /// 앞에 끼워 넣으면 위치 인자로 부르던 기존 호출이 조용히 어긋난다.
    /// (CancellationToken 은 스키마에 노출되지 않으므로 그 뒤에 오는 것은 상관없다.)
    /// </summary>
    [Fact]
    public void PortIsTheLastSchemaParameter()
    {
        foreach (var m in Tools())
        {
            string name = m.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            var visible = m.GetParameters()
                           .Where(p => p.ParameterType != typeof(CancellationToken))
                           .ToArray();

            Assert.Equal("port", visible[^1].Name);
            Assert.True(visible.Length >= 1, name);
        }
    }
}
