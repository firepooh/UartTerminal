namespace UartTerminal.Mcp;

/// <summary>
/// 앱 수준의 MCP HTTP 배선. 전송이 <b>앱당 하나</b>가 되면서 생긴 자리다 —
/// 파이프는 탭(<c>UartDocumentView</c>)이 각자 들고 있으면 됐지만, HTTP 엔드포인트는 그럴 수 없다.
///
/// <para>정책: <b>노출된 포트가 0 이면 소켓을 열지 않는다.</b> MCP 를 안 쓰는 사용자에게는
/// 지금과 체감이 같고(포트가 열려 있지 않다), 켠 사람에게만 열린다.
/// 그래서 <see cref="BridgeRegistry.ExposedCountChanged"/> 가 0↔1 을 넘을 때만 호스트를 켜고 끈다.</para>
///
/// <para>정적인 이유는 창(<c>ShellWindow</c>)이 여럿이어도 프로세스가 하나이기 때문이다.
/// 판단 로직은 전부 <see cref="BridgeRegistry"/>·<see cref="McpHttpServer"/> 안에 있고
/// 여기는 배선만 한다 — 그래야 테스트가 정적 상태를 건드리지 않는다.</para>
/// </summary>
internal static class McpHost
{
    public static BridgeRegistry Registry { get; } = new();

    public static McpHttpServer? Server { get; private set; }

    /// <summary>노출 수가 바뀌어 호스트 상태가 달라졌다 — 상태 표시줄 갱신 신호.</summary>
    public static event Action? StateChanged;

    /// <summary>앱 시작 시 한 번. 소켓은 아직 열지 않는다(포트를 노출해야 열린다).</summary>
    public static void Init(int httpPort)
    {
        if (Server is not null) return;
        Server = new McpHttpServer(Registry, httpPort);
        Registry.ExposedCountChanged += OnExposedCountChanged;
    }

    /// <summary>앱 종료 시. 살아 있는 연결이 종료를 붙잡지 않게 호스트를 명시적으로 내린다.</summary>
    public static void Shutdown()
    {
        if (Server is null) return;
        Registry.ExposedCountChanged -= OnExposedCountChanged;
        Server.Stop();
    }

    private static void OnExposedCountChanged(int count)
    {
        if (Server is null) return;

        if (count > 0 && Server.State != McpHttpState.Running) Server.Start();
        else if (count == 0 && Server.State == McpHttpState.Running) Server.Stop();

        StateChanged?.Invoke();
    }
}
