using System.Collections.Generic;
using System.Linq;

namespace UartTerminal.Mcp;

/// <summary>
/// 툴 호출이 어느 <see cref="UartBridge"/> 로 갈지 못 정한 이유.
/// 문장은 만들지 않는다 — 사유만 돌려주고 문구는 툴 계층(<see cref="UartMcpTools"/>)이 조립한다.
/// </summary>
public enum BridgeResolution
{
    Ok,
    /// <summary>MCP 에 노출된 포트가 하나도 없다.</summary>
    NoneExposed,
    /// <summary>port 를 생략했는데 노출된 포트가 둘 이상이다 — 추측하지 않는다.</summary>
    Ambiguous,
    /// <summary>지정한 port 가 노출 목록에 없다.</summary>
    NotFound,
}

/// <summary>
/// 툴 호출의 <c>port</c> 인자를 브리지로 바꾼다. 엔드포인트가 앱당 하나라서
/// 어느 포트로 갈지 고르는 단계가 필요하다 — 그 판단이 <see cref="BridgeRegistry"/> 에 있다.
/// </summary>
public interface IBridgeResolver
{
    /// <summary>MCP 에 노출 중인 포트명(정렬됨). 오류 메시지에 목록으로 실어 보낸다.</summary>
    IReadOnlyList<string> ExposedPorts { get; }

    bool TryResolve(string? port, out UartBridge? bridge, out BridgeResolution reason);
}

/// <summary>
/// 앱 전역의 <c>포트명 → UartBridge</c> 표. HTTP 엔드포인트가 앱당 하나뿐이라
/// (docs/mcp-http-transport.md §3.1) 어느 포트인지 고를 수단이 필요해 생겼다.
///
/// <para><b>등록 = 노출</b>이다. 탭이 MCP 를 켤 때 넣고 끌 때/닫을 때 뺀다.
/// 읽기 계열 툴(uart_read·uart_status·uart_screen·uart_expect)에는 브리지 안에 활성 검사가 없어서
/// (쓰기 계열만 <c>_enabled</c> 를 본다) 여기 남아 있는 것만으로 읽힌다 —
/// 엔드포인트를 공유하므로 <b>표에서 빼는 것이 곧 차단</b>이다.</para>
///
/// <para>툴은 스레드풀(MTA)에서 불리고 등록/해제는 UI 스레드에서 일어난다 — 그래서 전부 잠근다.</para>
/// </summary>
public sealed class BridgeRegistry : IBridgeResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UartBridge> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>노출 수가 바뀌었다(인자 = 바뀐 뒤 개수). 0↔1 을 넘을 때 HTTP 호스트를 켜고 끈다.</summary>
    public event Action<int>? ExposedCountChanged;

    public int Count { get { lock (_gate) return _map.Count; } }

    public IReadOnlyList<string> ExposedPorts
    {
        get
        {
            lock (_gate) return _map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>포트를 노출한다. 같은 포트명이 이미 있으면 새 브리지로 덮는다(탭이 포트를 이어받은 경우).</summary>
    public void Register(string port, UartBridge bridge)
    {
        string key = port.Trim();
        if (key.Length == 0) return;

        int count;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing) && ReferenceEquals(existing, bridge)) return;
            _map[key] = bridge;
            count = _map.Count;
        }
        ExposedCountChanged?.Invoke(count);
    }

    /// <summary>
    /// 노출을 거둔다. <paramref name="bridge"/> 가 지금 등록된 것과 다르면 아무것도 하지 않는다 —
    /// 닫히는 탭이 같은 포트명을 이어받은 다른 탭의 등록을 지워 버리는 사고를 막는다.
    /// </summary>
    public bool Unregister(string port, UartBridge bridge)
    {
        string key = port.Trim();
        int count;
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var existing) || !ReferenceEquals(existing, bridge)) return false;
            _map.Remove(key);
            count = _map.Count;
        }
        ExposedCountChanged?.Invoke(count);
        return true;
    }

    /// <summary>
    /// docs/mcp-http-transport.md §3.3 의 규칙 그대로다.
    /// 명시하면 그 포트 · 생략 + 1개면 그 포트 · 생략 + 2개 이상이면 <b>거절</b>.
    /// 마지막이 핵심이다: 마스터·슬레이브를 같이 물린 상태에서 하나를 골라 주면
    /// 엉뚱한 보드로 명령이 나간다. 애매하면 목록을 돌려주고 물러선다.
    /// </summary>
    public bool TryResolve(string? port, out UartBridge? bridge, out BridgeResolution reason)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(port))
            {
                if (_map.TryGetValue(port.Trim(), out var hit))
                {
                    bridge = hit;
                    reason = BridgeResolution.Ok;
                    return true;
                }
                bridge = null;
                reason = BridgeResolution.NotFound;
                return false;
            }

            bridge = null;
            reason = _map.Count switch
            {
                0 => BridgeResolution.NoneExposed,
                1 => BridgeResolution.Ok,
                _ => BridgeResolution.Ambiguous,
            };
            if (reason != BridgeResolution.Ok) return false;

            bridge = _map.Values.First();
            return true;
        }
    }
}
