using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;
using UartTerminal.Mcp;

namespace UartTerminal;

/// <summary>세션 오픈 결과.</summary>
public enum OpenOutcome { Success, InUse, Failed }

/// <summary>
/// UART 연결 수명주기 상태머신(UI 비의존). 세션 생성/오픈/종료, 자동 재연결 판단, MCP 포트 양보/재개,
/// 그리고 <b>지연된 Closed 콜백 무력화</b>(USB 분리 시 Closed 가 최대 1.5s 늦게 오는 문제)를 한곳에서 관리한다.
///
/// WPF 에 의존하지 않으므로 가짜 <see cref="ISerialSession"/> + 수동 tick 으로 단위 테스트할 수 있다.
/// 호스트(UartDocumentView)는 팝업·실제 DispatcherTimer·Dispatcher 마샬링·렌더만 담당하고,
/// 아래 콜백으로 배선한다:
///  - factory   : 현재 포트/속도로 세션 생성(<c>new SerialPortSession(...)</c> 또는 테스트 가짜)
///  - post      : Closed 콜백을 컨트롤러 스레드(UI)로 마샬링(<c>Dispatcher.BeginInvoke</c>)
///  - notify    : 상태 변경 알림 → 호스트가 제목/메트릭/재연결 타이머를 동기화
///  - status    : 상태바 메시지
///  - autoReconnect : 전역 자동 재연결 설정(다른 창에서 꺼도 매 판단마다 재확인)
///  - portName  : 현재 포트명(가드/메시지용)
/// </summary>
public sealed class ConnectionController
{
    private readonly TerminalEngine _engine;
    private readonly UartBridge _bridge;
    private readonly Func<ISerialSession> _factory;
    private readonly Func<string> _portName;
    private readonly Action<Action> _post;
    private readonly Func<bool> _autoReconnect;
    private readonly Action _notify;
    private readonly Action<string> _status;

    private ISerialSession? _session;
    private bool _connected;
    private bool _reconnectPending;
    private bool _mcpReleased;   // AI(MCP)가 외부 작업(플래싱 등)을 위해 포트를 양보한 상태
    private bool _closed;        // 문서 폐기됨 — 지연된 Closed 콜백의 재무장을 차단
    private string? _lastOpenError;

    public ConnectionController(
        TerminalEngine engine, UartBridge bridge,
        Func<ISerialSession> factory, Func<string> portName,
        Action<Action> post, Func<bool> autoReconnect,
        Action notify, Action<string> status)
    {
        _engine = engine;
        _bridge = bridge;
        _factory = factory;
        _portName = portName;
        _post = post;
        _autoReconnect = autoReconnect;
        _notify = notify;
        _status = status;
    }

    public bool IsConnected => _connected;
    public bool IsReconnecting => _reconnectPending;
    public bool IsPortReleased => _mcpReleased;
    public bool IsClosed => _closed;
    public string? LastOpenError => _lastOpenError;

    /// <summary>현재 세션에 송신 데이터 적재(키 입력/DSR 응답 등). 세션 없으면 무시.</summary>
    public void Enqueue(ReadOnlyMemory<byte> data) => _session?.Enqueue(data);

    /// <summary>
    /// 제어선 시퀀스(ESP32 하드웨어 리셋/부트로더 진입)를 현재 세션에 적용한다.
    /// 단계 사이에 대기가 있어 비동기이며, 도중 세션이 바뀌면 중단한다(엉뚱한 보드에 펄스가 가지 않게).
    /// </summary>
    public async Task<bool> ApplyControlLinesAsync(IReadOnlyList<ControlLineStep> steps,
                                                   CancellationToken ct = default)
    {
        var s = _session;
        if (s is null || !s.IsOpen) return false;

        foreach (var step in steps)
        {
            if (!ReferenceEquals(_session, s) || !s.IsOpen) return false;
            s.SetDtrRts(step.Dtr, step.Rts);
            if (step.DelayMs > 0)
                await Task.Delay(step.DelayMs, ct).ConfigureAwait(true);
        }
        return true;
    }

    // ── 오픈 ─────────────────────────────────────────────────────────────────────

    /// <summary>세션 오픈 핵심(조용함: 팝업/포커스 없음). 성공 시 세션 설정 + 'AI 양보' 해제.</summary>
    public OpenOutcome Open()
    {
        var session = _factory();
        void OnClosedLocal(SerialCloseReason reason) => _post(() => HandleSessionClosed(session, reason));
        void OnData(ReadOnlyMemory<byte> data) => Receive(data);
        session.DataReceived += OnData;
        session.Closed += OnClosedLocal;
        _bridge.AttachSession(session);
        try
        {
            session.Open();
        }
        catch (UnauthorizedAccessException)
        {
            DiscardFailedSession(session, OnData, OnClosedLocal);
            DiagLog.Warn($"포트 사용 중: {_portName()}");
            _notify();
            return OpenOutcome.InUse;
        }
        catch (Exception ex)
        {
            DiscardFailedSession(session, OnData, OnClosedLocal);
            DiagLog.Exception("OpenSession", ex);
            _lastOpenError = ex.Message;
            _notify();
            return OpenOutcome.Failed;
        }

        _session = session;
        _connected = true;
        _mcpReleased = false; // 어떤 경로로든 열림에 성공하면 'AI 양보' 상태 해제
        _engine.ResetParsing();
        DiagLog.Info($"연결됨: {_portName()}");
        _notify();
        return OpenOutcome.Success;
    }

    /// <summary>사용자 개시 오픈: 진행 중 자동 재연결을 끄고 'AI 양보'를 선(先)해제한 뒤 오픈(실패해도 [AI 양보] 잔존 방지).</summary>
    public OpenOutcome OpenUserInitiated()
    {
        StopAutoReconnect();
        _mcpReleased = false;
        return Open();
    }

    private void Receive(ReadOnlyMemory<byte> data)
    {
        if (DiagLog.Capture) DiagLog.Trace($"RX[{data.Length}] {DiagLog.Escape(data.Span)}");
        try { _engine.Receive(data.Span); }
        catch (Exception ex) { DiagLog.Exception("Receive", ex); }
    }

    /// <summary>오픈 실패 세션 정리. Closed/DataReceived 구독을 먼저 끊어 Dispose 시 오발화를 막는다.</summary>
    private void DiscardFailedSession(ISerialSession session,
        Action<ReadOnlyMemory<byte>> onData, Action<SerialCloseReason> onClosed)
    {
        session.Closed -= onClosed;
        session.DataReceived -= onData;
        _connected = false;
        _bridge.DetachSession();
        try { session.Dispose(); } catch { }
        _notify();
    }

    // ── 종료 콜백(호스트가 UI 스레드로 마샬링해 호출) ─────────────────────────────

    /// <summary>
    /// 세션 Closed 처리. 이 콜백이 '현재' 활성 세션의 것이 아니면(교체/사용자 해제/문서 폐기) 무시한다.
    /// USB 분리 시 Closed 는 DisposePortSafely(최대 1.5s) 이후 지연 발생하므로, 그 사이 사용자가
    /// 닫기/해제한 경우 여기서 자동 재연결 재무장을 반드시 차단해야 한다(핵심 불변식).
    /// </summary>
    public void HandleSessionClosed(ISerialSession closed, SerialCloseReason reason)
    {
        if (_closed || !ReferenceEquals(closed, _session))
            return;

        _connected = false;
        _session = null;
        _bridge.DetachSession();
        _notify();
        switch (reason)
        {
            case SerialCloseReason.DeviceRemoved:
                DiagLog.Warn($"장치 분리됨: {_portName()}");
                if (_autoReconnect() && !string.IsNullOrEmpty(_portName()))
                    StartAutoReconnect();
                else
                    _status("장치 분리됨 — Alt+N 또는 [터미널>재연결]");
                break;
            case SerialCloseReason.UserClosed:
                StopAutoReconnect();
                _status("연결 해제됨");
                break;
            default:
                _status("연결 종료(오류)");
                break;
        }
    }

    // ── 자동 재연결(대기 상태만 소유; 실제 폴링 타이머는 호스트) ─────────────────

    private void StartAutoReconnect()
    {
        if (_closed) return;
        _reconnectPending = true;
        _notify(); // 호스트가 IsReconnecting 을 보고 DispatcherTimer 를 시작
        _status($"장치 분리됨 — 자동 재연결 대기 중… ({_portName()})");
        DiagLog.Info($"자동 재연결 대기 시작: {_portName()}");
    }

    private void StopAutoReconnect()
    {
        if (!_reconnectPending) return;
        _reconnectPending = false;
        _notify(); // 호스트가 타이머를 정지
    }

    /// <summary>설정에서 자동 재연결을 끌 때 진행 중인 대기를 취소.</summary>
    public void CancelAutoReconnect()
    {
        if (!_reconnectPending) return;
        StopAutoReconnect();
        _status("자동 재연결 꺼짐 — Alt+N 또는 [터미널>재연결]");
    }

    /// <summary>
    /// 연결 상태에서 포트가 목록에서 사라진 것을 호스트 감시가 발견했을 때(유휴 케이블 뽑기).
    /// 유휴 중에는 ReadAsync 가 즉시 faulting 하지 않아 세션 Closed 가 안 오므로, 여기서 DeviceRemoved 로 취급한다.
    /// </summary>
    public void HandlePortVanished()
    {
        if (_closed || !_connected || _session is null) return;

        var s = _session;
        _session = null;
        _connected = false;
        _bridge.DetachSession();
        _notify();
        DiagLog.Warn($"포트 사라짐 감지(유휴): {_portName()}");
        if (_autoReconnect() && !string.IsNullOrEmpty(_portName()))
            StartAutoReconnect();
        else
            _status("장치 분리됨 — Alt+N 또는 [터미널>재연결]");
        // 죽은 포트 핸들 정리(늦게 오는 Closed 콜백은 _session=null 이라 가드가 무시).
        if (s is not null) { try { s.Close(); } catch { } }
    }

    /// <summary>호스트의 재연결 타이머 tick 에서 호출. portExists 는 호스트가 <c>PortEnumerator.PortExists</c> 로 판단.</summary>
    public void ReconnectTick(bool portExists)
    {
        // 전역 설정(autoReconnect)을 매 tick 재확인 — 다른 창에서 꺼도 스스로 종료.
        if (_closed || !_reconnectPending || !_autoReconnect() || _connected || string.IsNullOrEmpty(_portName()))
        {
            StopAutoReconnect();
            return;
        }

        if (!portExists)
            return; // 아직 안 나타남 — 계속 대기

        switch (Open())
        {
            case OpenOutcome.Success:
                StopAutoReconnect();
                _status($"자동 재연결됨: {_portName()}");
                DiagLog.Info($"자동 재연결됨: {_portName()}");
                break;
            case OpenOutcome.InUse:
                _status($"재연결 대기 중… ({_portName()} 사용 중)");
                break;
            default:
                _status($"재연결 대기 중… ({_portName()} 준비 중)");
                break;
        }
    }

    // ── 사용자 해제 ───────────────────────────────────────────────────────────────

    public void Disconnect()
    {
        StopAutoReconnect();
        // 상태를 '즉시' 정리해 _session 을 비운다 → 이미 큐잉된(지연된) DeviceRemoved 콜백이
        // 낡은 세션의 것이 되어 HandleSessionClosed 가드에서 무시된다(사용자 해제 후 원치 않는 자동 재연결 방지).
        var s = _session;
        _session = null;
        _connected = false;
        _mcpReleased = false;
        _bridge.DetachSession();
        _notify();
        _status("연결 해제됨");
        if (s is not null) { try { s.Close(); } catch { } }
    }

    /// <summary>재연결 다이얼로그 등에서 기존 세션만 조용히 닫기(교체 직전).</summary>
    public void CloseCurrentSession()
    {
        var s = _session;
        if (s is null) return;
        _session = null;
        _connected = false;
        _bridge.DetachSession();
        try { s.Close(); } catch { }
        _notify();
    }

    // ── MCP 포트 양보/재개(uart_close/uart_open) ─────────────────────────────────

    public PortActionResult McpRelease()
    {
        if (_closed || string.IsNullOrEmpty(_portName()))
            return new PortActionResult { Ok = false, Port = _portName(), State = "error", Error = "no_port" };

        StopAutoReconnect(); // AI 가 명시적으로 닫음 — USB 감시 폴링이 포트를 도로 잡지 않게 중단
        var s = _session;
        _session = null; // Disconnect 와 동일 방침 — 지연 Closed 콜백 무력화
        _connected = false;
        bool wasOpen = s is not null;
        _mcpReleased = true;
        _bridge.DetachSession();
        _notify();
        _status($"AI가 포트 양보 — 외부 작업 대기 중… ({_portName()})");
        DiagLog.Info($"MCP 포트 양보(uart_close): {_portName()}");
        if (s is not null) { try { s.Close(); } catch { } }

        return new PortActionResult
        {
            Ok = true,
            Connected = false,
            Port = _portName(),
            State = wasOpen ? "closed" : "already_closed",
        };
    }

    public PortActionResult McpReopen()
    {
        if (_closed || string.IsNullOrEmpty(_portName()))
            return new PortActionResult { Ok = false, Port = _portName(), State = "error", Error = "no_port" };

        if (_connected)
        {
            _mcpReleased = false;
            return new PortActionResult { Ok = true, Connected = true, Port = _portName(), State = "already_open" };
        }

        switch (Open())
        {
            case OpenOutcome.Success:
                StopAutoReconnect(); // 장치 분리 후 대기 중이었다면 함께 종료
                _status($"AI가 포트 재연결(uart_open): {_portName()}");
                DiagLog.Info($"MCP 포트 재연결(uart_open): {_portName()}");
                return new PortActionResult { Ok = true, Connected = true, Port = _portName(), State = "open" };
            case OpenOutcome.InUse:
                _status($"재연결 대기 — {_portName()} 아직 사용 중(외부 작업 진행 중?)");
                return new PortActionResult { Ok = false, Connected = false, Port = _portName(), State = "in_use", Error = "in_use" };
            default:
                _status($"재연결 실패: {_lastOpenError}");
                return new PortActionResult { Ok = false, Connected = false, Port = _portName(), State = "error", Error = _lastOpenError ?? "open_failed" };
        }
    }

    // ── 폐기 ─────────────────────────────────────────────────────────────────────

    /// <summary>문서/탭이 닫힐 때. _closed 로 이후 지연 콜백의 재무장을 차단하고 세션을 닫는다.</summary>
    public void CloseDocument()
    {
        _closed = true;
        StopAutoReconnect();
        var s = _session;
        _session = null;
        if (s is not null) { try { s.Close(); } catch { } }
    }
}
