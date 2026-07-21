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

/// <summary>
/// MCP 툴과 WPF 앱 사이의 스레드 안전 파사드(README 기능 3). 시리얼 세션·터미널 엔진·수신 링버퍼·접근제어
/// 상태를 한곳에서 감싸, MCP 서버 스레드에서 호출되는 6개 툴이 안전하게 포트를 공유하도록 한다.
///  - TX 는 단일 큐(<see cref="ISerialSession.Enqueue"/>)로 직렬화되어 사용자 입력과 원자적으로 섞인다.
///  - AI 송신은 화면에 <c>[AI→]</c> 메타 라인으로 표시(수신 스트림과 구분).
///  - 접근제어: 비활성/읽기전용에서 TX·제어선 변경을 차단.
/// COM 포트는 한 프로세스만 열 수 있어 MCP 서버는 반드시 in-process 여야 한다(README §4.2).
/// </summary>
public sealed class UartBridge
{
    private const int RingCapacity = 1 << 20;   // 1 MiB 수신 링버퍼
    private const int ExpectChunk = 64 * 1024;   // expect 한 번에 훑는 최대 바이트
    private const int ExpectMaxAccum = 256 * 1024; // 정규식 매칭용 누적 텍스트 상한(문자)

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

    public UartBridge(TerminalEngine engine) => _engine = engine;

    /// <summary>MCP 서버 활성 여부(꺼지면 파이프 리스너가 닫히고 툴 호출도 거부).</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>AI 읽기 전용(TX/제어선 변경 차단, 읽기·상태·화면은 허용).</summary>
    public bool ReadOnly { get => _readOnly; set => _readOnly = value; }

    public string PortName { get { lock (_gate) return _portName; } }

    public event Action? StateChanged;

    // ── 세션 수명주기(UI 스레드에서 호출) ───────────────────────────────────────

    /// <summary>새 세션을 연결하고 수신 tee 를 링버퍼에 구독. 재연결 시 링버퍼는 초기화.</summary>
    public void AttachSession(ISerialSession session)
    {
        lock (_gate)
        {
            DetachLocked();
            _session = session;
            _portName = session.PortName;
            _ring.Clear();
            Action<ReadOnlyMemory<byte>> handler = OnRx;
            _rxHandler = handler;
            session.DataReceived += handler;
        }
        RaiseStateChanged();
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

        // 개행을 Transmit New-line(CR)로 정규화(붙여넣기 경로와 동일 방침).
        string body = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (appendNewline && !body.EndsWith('\r')) body += "\r";

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

    public async Task<ExpectResult> ExpectAsync(string pattern, int timeoutMs, long? cursor,
        bool stripAnsi, bool useRegex, CancellationToken ct)
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

                Match m;
                try { m = rx.Match(accum.ToString()); }
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

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 링버퍼 조각을 UTF-8 텍스트로 디코드하되, 멀티바이트 문자 경계에서 잘리지 않도록 불완전한 후행 바이트는
    /// 다음 읽기로 미룬다(커서를 완전한 경계까지만 전진). strip_ansi 시 이스케이프/제어를 제거.
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
        string text = stripAnsi ? AnsiText.Strip(raw) : raw;
        return (text, slice.Cursor - back);
    }

    private static string SanitizeForDisplay(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c is '\r' or '\n' ? ' ' : (c < 0x20 || c == 0x7F ? '·' : c));
        return sb.ToString().Trim();
    }
}
