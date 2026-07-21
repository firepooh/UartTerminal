namespace UartTerminal.Core.Serial;

/// <summary>
/// 링버퍼에서 잘라낸 원시 수신 바이트 조각과 커서 메타데이터(README §5 "MCP용 링버퍼").
/// <see cref="Cursor"/>는 이번에 반환한 마지막 바이트 다음 위치(다음 호출에 그대로 넘기면 이어서 읽음).
/// <see cref="Dropped"/>는 요청 커서와 실제 시작 사이에 링버퍼 용량 초과로 유실된 바이트 수.
/// <see cref="End"/>는 현재까지 수신된 총 바이트(커서 &lt; End 이면 아직 읽지 않은 데이터가 남음).
/// </summary>
public readonly struct RxSlice
{
    public readonly byte[] Data;
    public readonly long Cursor;
    public readonly long Dropped;
    public readonly long End;

    public RxSlice(byte[] data, long cursor, long dropped, long end)
    {
        Data = data;
        Cursor = cursor;
        Dropped = dropped;
        End = end;
    }
}

/// <summary>링버퍼의 일관된 순간 상태(한 락 안에서 캡처). <see cref="Oldest"/> = <see cref="Total"/> - <see cref="Count"/>.</summary>
public readonly record struct RingState(long Total, int Count, long Oldest);

/// <summary>
/// MCP <c>uart_read</c>/<c>uart_expect</c>가 읽는 원시 수신 바이트의 순환 버퍼.
/// 시리얼 tee 지점(<see cref="ISerialSession.DataReceived"/>)에서 <see cref="Append"/>로 채우고,
/// AI는 <b>단조 증가 절대 커서</b>(수신 시작 이후 총 바이트 오프셋)로 읽는다.
/// 용량을 넘긴 오래된 바이트는 덮어써지며, 그만큼 <see cref="RxSlice.Dropped"/>로 명시해 유실을 숨기지 않는다.
/// 화면 버퍼(<see cref="Terminal.TerminalBuffer"/>)와 독립이라 로깅 기능 없이도 AI가 읽을 버퍼를 확보한다.
/// 모든 접근은 내부 락으로 스레드 안전(시리얼 워커 스레드 append / MCP 스레드 read).
/// </summary>
public sealed class RxRingBuffer
{
    private readonly object _sync = new();
    private readonly byte[] _buf;
    private readonly int _capacity;
    private int _head;        // 다음 쓰기 위치(순환)
    private int _count;       // 현재 보관 중인 바이트 수(<= capacity)
    private long _total;      // 수신 시작 이후 총 append 바이트(단조 증가)

    public RxRingBuffer(int capacity = 1 << 20) // 기본 1 MiB
    {
        _capacity = Math.Max(4096, capacity);
        _buf = new byte[_capacity];
    }

    public int Capacity => _capacity;

    /// <summary>수신 시작 이후 총 바이트(단조 증가). = 다음에 도착할 바이트의 커서.</summary>
    public long Total { get { lock (_sync) return _total; } }

    /// <summary>현재 보관 중인 가장 오래된 바이트의 커서(이보다 앞은 유실).</summary>
    public long Oldest { get { lock (_sync) return _total - _count; } }

    /// <summary>현재 보관 중인 바이트 수.</summary>
    public int Count { get { lock (_sync) return _count; } }

    /// <summary>Total/Count/Oldest 를 한 락 안에서 함께 캡처한 일관 스냅샷(상태 보고용 — 조각난 읽기로 인한 음수 커서 방지).</summary>
    public RingState Snapshot()
    {
        lock (_sync)
            return new RingState(_total, _count, _total - _count);
    }

    /// <summary>수신 바이트를 링버퍼에 적재. 시리얼 워커 스레드에서 호출(tee).</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_sync)
        {
            long originalLen = data.Length; // 유실분 계산을 위해 실제 도착 총량을 누적

            // 한 번에 용량 이상이 오면 마지막 capacity 바이트만 보관(그 앞은 즉시 유실)
            if (data.Length >= _capacity)
            {
                data[^_capacity..].CopyTo(_buf);
                _head = 0;              // 정확히 가득 참: 다음 쓰기/논리적 끝은 물리 0
                _count = _capacity;
                _total += originalLen;
                return;
            }

            // 최대 2구간 블록 복사(끝까지 + 앞으로 랩)
            int first = Math.Min(data.Length, _capacity - _head);
            data.Slice(0, first).CopyTo(_buf.AsSpan(_head));
            int rest = data.Length - first;
            if (rest > 0)
                data.Slice(first, rest).CopyTo(_buf.AsSpan(0));

            _head = (_head + data.Length) % _capacity;
            _count = Math.Min(_capacity, _count + data.Length);
            _total += originalLen;
        }
    }

    /// <summary>
    /// <paramref name="fromCursor"/>부터 최대 <paramref name="maxBytes"/> 바이트를 읽어 반환.
    /// 요청 커서가 보관 범위보다 오래됐으면 가장 오래된 위치로 당기고 유실분을 <see cref="RxSlice.Dropped"/>로 보고.
    /// 요청 커서가 현재 끝을 넘으면 빈 조각(끝으로 정렬)을 반환.
    /// </summary>
    public RxSlice Read(long fromCursor, int maxBytes)
    {
        if (maxBytes < 0) maxBytes = 0;
        lock (_sync)
        {
            long oldest = _total - _count;
            long end = _total;

            long start = fromCursor;
            long dropped = 0;
            if (start < oldest)
            {
                dropped = oldest - start;
                start = oldest;
            }
            if (start > end) start = end;

            long available = end - start;
            int n = (int)Math.Min(available, maxBytes);
            var outBuf = new byte[n];

            // start 의 링버퍼 물리 인덱스: head 는 end 위치이므로 end-start 만큼 뒤로.
            // (start..end) 구간은 항상 보관 범위 안(위에서 clamp).
            int back = (int)(end - start);
            int idx = ((_head - back) % _capacity + _capacity) % _capacity;
            for (int i = 0; i < n; i++)
            {
                outBuf[i] = _buf[idx];
                idx = (idx + 1) % _capacity;
            }

            return new RxSlice(outBuf, start + n, dropped, end);
        }
    }

    /// <summary>재연결 등에서 초기화(커서도 0으로).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
            _total = 0;
        }
    }
}
