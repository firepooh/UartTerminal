using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UartTerminal.Core.Serial;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Mcp;

// ── MCP 툴 반환 DTO (snake_case JSON) ───────────────────────────────────────────

public sealed record StatusResult
{
    [JsonPropertyName("port")] public string Port { get; init; } = "";
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("mcp_enabled")] public bool McpEnabled { get; init; }
    [JsonPropertyName("read_only")] public bool ReadOnly { get; init; }
    [JsonPropertyName("baud")] public int Baud { get; init; }
    [JsonPropertyName("line")] public string Line { get; init; } = "";
    [JsonPropertyName("dtr")] public bool Dtr { get; init; }
    [JsonPropertyName("rts")] public bool Rts { get; init; }
    [JsonPropertyName("total_received_bytes")] public long TotalReceivedBytes { get; init; }
    [JsonPropertyName("retained_bytes")] public int RetainedBytes { get; init; }
    [JsonPropertyName("oldest_cursor")] public long OldestCursor { get; init; }
    [JsonPropertyName("end_cursor")] public long EndCursor { get; init; }
}

public sealed record SendResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("bytes_sent")] public int BytesSent { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record ReadResult
{
    [JsonPropertyName("data")] public string Data { get; init; } = "";
    [JsonPropertyName("cursor")] public long Cursor { get; init; }
    [JsonPropertyName("dropped_bytes")] public long DroppedBytes { get; init; }
    [JsonPropertyName("end_cursor")] public long EndCursor { get; init; }
    [JsonPropertyName("more")] public bool More { get; init; }
    [JsonPropertyName("connected")] public bool Connected { get; init; }
}

public sealed record ExpectResult
{
    [JsonPropertyName("matched")] public bool Matched { get; init; }
    [JsonPropertyName("timed_out")] public bool TimedOut { get; init; }
    [JsonPropertyName("match")] public string? Match { get; init; }
    [JsonPropertyName("groups")] public string[]? Groups { get; init; }
    [JsonPropertyName("data")] public string Data { get; init; } = "";
    [JsonPropertyName("cursor")] public long Cursor { get; init; }
    [JsonPropertyName("dropped_bytes")] public long DroppedBytes { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record ScreenResult
{
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("line_count")] public int LineCount { get; init; }
    [JsonPropertyName("total_lines")] public long TotalLines { get; init; }
}

public sealed record DtrRtsResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("dtr")] public bool Dtr { get; init; }
    [JsonPropertyName("rts")] public bool Rts { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record ResetResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    /// <summary>hard(EN 펄스) / bootloader(다운로드 모드 진입)</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "";
    [JsonPropertyName("dtr")] public bool Dtr { get; init; }
    [JsonPropertyName("rts")] public bool Rts { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record PortActionResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("port")] public string Port { get; init; } = "";
    /// <summary>closed / already_closed / open / already_open / in_use / error</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// MCP 툴과 WPF 앱 사이의 스레드 안전 파사드(README 기능 3). 시리얼 세션·터미널 엔진·수신 링버퍼·접근제어
/// 상태를 한곳에서 감싸, MCP 서버 스레드에서 호출되는 9개 툴이 안전하게 포트를 공유하도록 한다.
///  - TX 는 단일 큐(<see cref="ISerialSession.Enqueue"/>)로 직렬화되어 사용자 입력과 원자적으로 섞인다.
///  - AI 송신은 화면에 <c>[AI→]</c> 메타 라인으로 표시(수신 스트림과 구분).
///  - 접근제어: 비활성/읽기전용에서 TX·제어선 변경·포트 열기/닫기를 차단.
///  - 포트 열기/닫기(uart_open/uart_close)는 UI 스레드의 문서가 소유하므로 델리게이트로 위임한다.
/// COM 포트는 한 프로세스만 열 수 있어 MCP 서버는 반드시 in-process 여야 한다(README §4.2).
/// </summary>
public sealed class UartBridge
{
    private const int RingCapacity = 1 << 20;   // 1 MiB 수신 링버퍼
    private const int ExpectChunk = 64 * 1024;   // expect 한 번에 훑는 최대 바이트
    private const int ExpectMaxAccum = 256 * 1024; // 정규식 매칭용 누적 텍스트 상한(문자)

    /// <summary>줄 완성 판정용 개행 문자. strip_ansi 면 LF 로 정규화되지만, 원문 그대로일 땐 CR 만 오는 장비도 있다.</summary>
    private static readonly char[] NewlineChars = { '\n', '\r' };

    private readonly object _gate = new();
    private readonly Encoding _utf8 = new UTF8Encoding(false);
    private readonly RxRingBuffer _ring = new(RingCapacity);
    private readonly TerminalEngine _engine;

    private ISerialSession? _session;
    private Action<ReadOnlyMemory<byte>>? _rxHandler;
    private string _portName = "";

    // 데이터 도착 신호(expect 대기용). Read 전에 캡처해 lost-wakeup 방지.
    private volatile TaskCompletionSource _dataSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _enabled;
    private volatile bool _readOnly;
    private volatile TransmitNewline _txNewline = TransmitNewline.Cr;

    // 포트 열기/닫기 핸들러(문서가 UI 스레드에서 Dispatcher 로 마샬링하여 수행). null 이면 not_supported.
    private volatile Func<Task<PortActionResult>>? _closeHandler;
    private volatile Func<Task<PortActionResult>>? _openHandler;

    public UartBridge(TerminalEngine engine) => _engine = engine;

    /// <summary>MCP 서버 활성 여부(꺼지면 파이프 리스너가 닫히고 툴 호출도 거부).</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>AI 읽기 전용(TX/제어선 변경 차단, 읽기·상태·화면은 허용).</summary>
    public bool ReadOnly { get => _readOnly; set => _readOnly = value; }

    /// <summary>AI 송신에 쓸 개행 규약(사용자 설정과 동일하게 유지 — 장치가 LF 를 기대하면 AI 도 LF).</summary>
    public TransmitNewline TransmitNewline { get => _txNewline; set => _txNewline = value; }

    public string PortName { get { lock (_gate) return _portName; } }

    public event Action? StateChanged;

    /// <summary>포트 열기/닫기 핸들러를 등록한다(문서가 UI 스레드 마샬링을 담당). uart_open/uart_close 가 이를 호출.</summary>
    public void SetPortController(Func<Task<PortActionResult>> closeHandler, Func<Task<PortActionResult>> openHandler)
    {
        _closeHandler = closeHandler;
        _openHandler = openHandler;
    }

    // ── 세션 수명주기(UI 스레드에서 호출) ───────────────────────────────────────

    /// <summary>
    /// 새 세션을 연결하고 수신 tee 를 링버퍼에 구독.
    /// <b>링버퍼는 지우지 않는다</b> — 오픈 성공이 확정된 뒤 <see cref="ResetRing"/> 로 지운다.
    /// 여기서 지웠더니 <i>실패한</i> 오픈도 버퍼를 비웠고, 자동 재연결이 1.5초마다 재시도하므로
    /// 장치 분리 직전의 패닉 로그(AI 가 읽어야 할 것)가 첫 재시도에서 사라졌다.
    /// 커서까지 0 으로 돌아가 <c>uart_status</c> 의 유실 신호(dropped_bytes)도 남지 않았다.
    /// </summary>
    public void AttachSession(ISerialSession session)
    {
        lock (_gate)
        {
            DetachLocked();
            _session = session;
            _portName = session.PortName;
            Action<ReadOnlyMemory<byte>> handler = OnRx;
            _rxHandler = handler;
            session.DataReceived += handler;
        }
        RaiseStateChanged();
    }

    /// <summary>오픈 성공이 확정된 뒤 링버퍼를 초기화(커서도 0). 실패한 오픈에서는 호출하지 않는다.</summary>
    public void ResetRing()
    {
        lock (_gate) _ring.Clear();
    }

    /// <summary>세션 분리(장치 제거/사용자 종료). 링버퍼 내용은 마지막 상태로 유지.</summary>
    public void DetachSession()
    {
        lock (_gate) DetachLocked();
        RaiseStateChanged();
    }

    private void DetachLocked()
    {
        if (_session is not null && _rxHandler is not null)
        {
            try { _session.DataReceived -= _rxHandler; } catch { }
        }
        _session = null;
        _rxHandler = null;
    }

    private void OnRx(ReadOnlyMemory<byte> data)
    {
        _ring.Append(data.Span);
        SignalData();
    }

    private void SignalData()
    {
        var tcs = Interlocked.Exchange(ref _dataSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    // ── 툴 구현 ──────────────────────────────────────────────────────────────────

    public StatusResult Status()
    {
        lock (_gate)
        {
            var s = _session;
            bool connected = s?.IsOpen ?? false;
            var rs = _ring.Snapshot(); // Total/Count/Oldest 를 한 락에서 일관 캡처(음수 커서 방지)
            var p = s?.Params;
            return new StatusResult
            {
                Port = _portName,
                Connected = connected,
                McpEnabled = _enabled,
                ReadOnly = _readOnly,
                Baud = p?.BaudRate ?? 0,
                Line = p?.Summary() ?? "",
                Dtr = s?.DtrEnabled ?? false,
                Rts = s?.RtsEnabled ?? false,
                TotalReceivedBytes = rs.Total,
                RetainedBytes = rs.Count,
                OldestCursor = rs.Oldest,
                EndCursor = rs.Total,
            };
        }
    }

    public SendResult Send(string text, bool appendNewline)
    {
        if (!_enabled) return new SendResult { Ok = false, Error = "mcp_disabled" };
        if (_readOnly) return new SendResult { Ok = false, Error = "read_only" };

        ISerialSession? s;
        lock (_gate) s = _session;
        if (s is null || !s.IsOpen) return new SendResult { Ok = false, Error = "disconnected" };

        // 개행을 Transmit New-line 설정값으로 정규화(사용자 붙여넣기 경로와 동일 방침).
        string nl = _txNewline.Text();
        string body = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", nl);
        if (appendNewline && !body.EndsWith(nl, StringComparison.Ordinal)) body += nl;

        byte[] bytes = _utf8.GetBytes(body);
        if (bytes.Length == 0) return new SendResult { Ok = true, BytesSent = 0 };

        s.Enqueue(bytes);                 // 단일 TX 큐 → 원자적 전송
        _engine.Buffer.AppendMetaLine($"[AI→] {SanitizeForDisplay(text)}");
        return new SendResult { Ok = true, BytesSent = bytes.Length };
    }

    public ReadResult Read(long? cursor, int maxBytes, bool stripAnsi)
    {
        if (maxBytes <= 0) maxBytes = 8192;
        maxBytes = Math.Min(maxBytes, ExpectChunk);

        long start = cursor ?? _ring.Oldest; // 커서 생략 시: 보관 중인 가장 오래된 위치부터(백로그)
        var slice = _ring.Read(start, maxBytes);
        var (text, next) = DecodeSlice(slice, stripAnsi);

        bool connected;
        lock (_gate) connected = _session?.IsOpen ?? false;

        return new ReadResult
        {
            Data = text,
            Cursor = next,
            DroppedBytes = slice.Dropped,
            EndCursor = slice.End,
            More = next < slice.End,
            Connected = connected,
        };
    }

    /// <summary>
    /// 패턴이 나타날 때까지 대기. <paramref name="lineMode"/>가 true 면 <b>완성된 줄까지만</b> 평가한다.
    /// <para>
    /// 스트림 평가(기본)는 청크가 도착할 때마다 매칭하므로, <c>mode\s+:\s+\w+</c> 같은 패턴이 "CDC" 의
    /// 첫 글자만 도착한 시점에 <c>mode : C</c> 로 <b>조기 매칭</b>된다. 줄 단위 로그를 기다릴 때는
    /// lineMode 로 이 함정을 없앨 수 있다. 다만 개행이 없는 프롬프트(<c>xcp&gt; </c>)는 영원히 완성되지
    /// 않으므로 그런 대기에는 기본(스트림) 모드를 써야 한다 — 그래서 기본값이 아니라 옵션이다.
    /// </para>
    /// </summary>
    public async Task<ExpectResult> ExpectAsync(string pattern, int timeoutMs, long? cursor,
        bool stripAnsi, bool useRegex, CancellationToken ct, bool lineMode = false)
    {
        if (pattern.Length > 2000)
            return new ExpectResult { Matched = false, TimedOut = false, Error = "bad_pattern: too long" };
        if (timeoutMs < 0) timeoutMs = 0;

        // 개별 Match 호출에 상한을 둬 병리적 백트래킹(ReDoS)이 서버 스레드를 무한 점유하지 못하게 한다.
        var matchTimeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs == 0 ? 1000 : timeoutMs, 200, 2000));
        Regex rx;
        try
        {
            rx = useRegex
                ? new Regex(pattern, RegexOptions.CultureInvariant, matchTimeout)
                : new Regex(Regex.Escape(pattern), RegexOptions.CultureInvariant, matchTimeout);
        }
        catch (Exception ex)
        {
            return new ExpectResult { Matched = false, TimedOut = false, Error = $"bad_pattern: {ex.Message}" };
        }

        long start = cursor ?? _ring.Total; // 커서 생략 시: 지금 이후 새로 도착하는 데이터를 기다림
        long firstDropped = 0;
        var accum = new StringBuilder();
        long deadlineTicks = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            // 데이터를 읽기 전에 다음 신호를 캡처(읽기와 대기 사이 도착분 유실 방지)
            Task signal = _dataSignal.Task;

            long readFrom = start;
            var slice = _ring.Read(readFrom, ExpectChunk);
            if (slice.Dropped > 0) firstDropped += slice.Dropped;
            var (text, next) = DecodeSlice(slice, stripAnsi);
            start = next;

            if (text.Length > 0)
            {
                accum.Append(text);
                if (accum.Length > ExpectMaxAccum)
                    accum.Remove(0, accum.Length - ExpectMaxAccum);

                // 줄 모드: 마지막 개행까지만 평가한다(개행이 아직 없으면 이번 회차는 평가하지 않음).
                // 도착 중인 토큰의 앞부분에 매칭되는 조기 매칭을 막는다.
                string haystack = accum.ToString();
                int evalTo = lineMode ? haystack.LastIndexOfAny(NewlineChars) + 1 : haystack.Length;
                if (evalTo > 0)
                {
                    if (evalTo < haystack.Length) haystack = haystack[..evalTo];

                    Match m;
                    try { m = rx.Match(haystack); }
                    catch (RegexMatchTimeoutException)
                    {
                        return new ExpectResult
                        {
                            Matched = false,
                            TimedOut = false,
                            Data = accum.ToString(),
                            Cursor = start,
                            DroppedBytes = firstDropped,
                            Error = "regex_timeout",
                        };
                    }

                    if (m.Success)
                    {
                        var groups = new string[m.Groups.Count];
                        for (int i = 0; i < m.Groups.Count; i++) groups[i] = m.Groups[i].Value;
                        return new ExpectResult
                        {
                            Matched = true,
                            TimedOut = false,
                            Match = m.Value,
                            Groups = groups,
                            Data = accum.ToString(),
                            Cursor = start,
                            DroppedBytes = firstDropped,
                        };
                    }
                }
            }

            long remaining = deadlineTicks - Environment.TickCount64;
            if (remaining <= 0 || ct.IsCancellationRequested)
            {
                return new ExpectResult
                {
                    Matched = false,
                    TimedOut = !ct.IsCancellationRequested,
                    Data = accum.ToString(),
                    Cursor = start,
                    DroppedBytes = firstDropped,
                    Error = ct.IsCancellationRequested ? "canceled" : null,
                };
            }

            // 커서가 실제로 전진했고(readFrom→next) 아직 못 읽은 데이터가 더 있을 때만 즉시 재시도.
            // 라이브 엣지의 불완전 UTF-8 조각으로 커서가 제자리면(next == readFrom) busy-spin 대신 신호/타임아웃 대기.
            if (next < slice.End && next != readFrom) continue;

            try
            {
                await Task.WhenAny(signal, Task.Delay((int)Math.Min(remaining, int.MaxValue), ct))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* 다음 루프에서 ct 처리 */ }
        }
    }

    public ScreenResult Screen(int maxLines)
    {
        if (maxLines <= 0) maxLines = 50;
        maxLines = Math.Min(maxLines, 2000);

        var buffer = _engine.Buffer;
        var sb = new StringBuilder();
        int emitted;
        long total;
        lock (buffer.SyncRoot)
        {
            int count = buffer.LineCount;
            total = buffer.TrimmedCount + count;
            int from = Math.Max(0, count - maxLines);
            emitted = count - from;
            for (int i = from; i < count; i++)
            {
                sb.Append(buffer.GetLine(i).Text());
                if (i < count - 1) sb.Append('\n');
            }
        }
        return new ScreenResult { Text = sb.ToString(), LineCount = emitted, TotalLines = total };
    }

    public DtrRtsResult SetDtrRts(bool dtr, bool rts)
    {
        if (!_enabled) return new DtrRtsResult { Ok = false, Error = "mcp_disabled" };
        if (_readOnly) return new DtrRtsResult { Ok = false, Error = "read_only" };

        ISerialSession? s;
        lock (_gate) s = _session;
        if (s is null || !s.IsOpen) return new DtrRtsResult { Ok = false, Error = "disconnected" };

        s.SetDtrRts(dtr, rts);
        return new DtrRtsResult { Ok = true, Dtr = s.DtrEnabled, Rts = s.RtsEnabled };
    }

    /// <summary>
    /// ESP32 리셋/부트로더 진입 시퀀스를 수행한다(uart_reset). 제어선을 직접 흔드는 것보다
    /// 타이밍(100ms/50ms)이 보장돼 AI 가 왕복 없이 한 번에 리셋할 수 있다.
    /// </summary>
    public async Task<ResetResult> ResetAsync(bool bootloader, CancellationToken ct = default)
    {
        string mode = bootloader ? "bootloader" : "hard";
        if (!_enabled) return new ResetResult { Ok = false, Mode = mode, Error = "mcp_disabled" };
        if (_readOnly) return new ResetResult { Ok = false, Mode = mode, Error = "read_only" };

        ISerialSession? s;
        lock (_gate) s = _session;
        if (s is null || !s.IsOpen) return new ResetResult { Ok = false, Mode = mode, Error = "disconnected" };

        var steps = bootloader ? EspResetSequence.Bootloader : EspResetSequence.HardReset;
        try
        {
            await EspResetSequence.ApplyAsync(s, steps, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ResetResult { Ok = false, Mode = mode, Error = "canceled" };
        }

        _engine.Buffer.AppendMetaLine(Loc.S(bootloader ? "Doc.AiBootloader" : "Doc.AiHardReset"));
        return new ResetResult { Ok = true, Mode = mode, Dtr = s.DtrEnabled, Rts = s.RtsEnabled };
    }

    /// <summary>포트를 닫아 외부 도구(esptool 등)에 양보. 실제 닫기는 문서가 UI 스레드에서 수행.</summary>
    public Task<PortActionResult> ClosePortAsync() => InvokePortHandler(_closeHandler);

    /// <summary>양보했던(또는 끊긴) 포트를 다시 연다. 실제 열기는 문서가 UI 스레드에서 수행.</summary>
    public Task<PortActionResult> OpenPortAsync() => InvokePortHandler(_openHandler);

    private async Task<PortActionResult> InvokePortHandler(Func<Task<PortActionResult>>? handler)
    {
        if (!_enabled) return new PortActionResult { Ok = false, Port = PortName, Error = "mcp_disabled" };
        if (_readOnly) return new PortActionResult { Ok = false, Port = PortName, Error = "read_only" };
        if (handler is null) return new PortActionResult { Ok = false, Port = PortName, Error = "not_supported" };
        try
        {
            return await handler().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 문서/디스패처 종료 경합 등 — 예외를 툴 결과로 정규화(서버 스레드 크래시 방지).
            return new PortActionResult { Ok = false, Port = PortName, Error = $"exception: {ex.Message}" };
        }
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 링버퍼 조각을 UTF-8 텍스트로 디코드하되, <b>경계에 걸린 것은 다음 읽기로 미룬다</b>(커서를 완전한
    /// 경계까지만 전진). 두 가지를 미룬다:
    /// <list type="number">
    ///   <item>불완전한 멀티바이트 UTF-8 후행 바이트 — 한글이 읽기 경계에서 깨지지 않게</item>
    ///   <item>strip_ansi 일 때 미완성 이스케이프 시퀀스 — <c>ESC[3</c>|<c>2m</c> 처럼 갈리면
    ///         뒤 조각(<c>2m</c>)이 리터럴 텍스트로 새어 AI 가 보는 데이터를 오염시키고 오탐 매칭을 만든다</item>
    /// </list>
    /// </summary>
    private (string text, long nextCursor) DecodeSlice(in RxSlice slice, bool stripAnsi)
    {
        int len = slice.Data.Length;
        if (len == 0) return ("", slice.Cursor);

        int complete = Utf8Boundary.CompleteLength(slice.Data);
        // 진전 보장: 완전한 문자가 하나도 없는데(=완전길이0) 링버퍼에 이미 더 있는 경우엔 전체를 소비.
        if (complete == 0 && slice.Cursor < slice.End) complete = len;
        if (complete == 0) return ("", slice.Cursor - len); // 라이브 엣지의 불완전 문자 → 다음 읽기로

        string raw = _utf8.GetString(slice.Data, 0, complete);
        long back = len - complete;

        if (!stripAnsi)
            return (raw, slice.Cursor - back);

        // 미완성 이스케이프 시퀀스는 잘라 두고 다음 읽기에서 이어 붙인다.
        int escComplete = AnsiText.CompleteLength(raw);
        if (escComplete < raw.Length)
        {
            // 라이브 엣지가 아니라면(뒤에 이미 더 있음) 보류해도 진전이 막히지 않는다.
            // 반대로 조각 전체가 미완성 시퀀스뿐이고 더 읽을 것도 없으면 그대로 보류(다음 신호를 기다림).
            back += _utf8.GetByteCount(raw.AsSpan(escComplete));
            raw = raw[..escComplete];
        }

        return (AnsiText.Strip(raw), slice.Cursor - back);
    }

    private static string SanitizeForDisplay(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c is '\r' or '\n' ? ' ' : (c < 0x20 || c == 0x7F ? '·' : c));
        return sb.ToString().Trim();
    }
}
