using System.Text;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Core.Logging;

/// <summary>연속 로깅 기록 형식.</summary>
public enum LogFormat
{
    /// <summary>
    /// 화면에 보이는 대로(기본). 터미널 엔진이 <b>이미 해석한</b> 논리 라인 텍스트를 줄이 끝날 때마다 쓴다 —
    /// 색·커서 이동·CR 덮어쓰기가 모두 반영된 결과라 파일이 화면과 같아진다.
    /// </summary>
    Screen,

    /// <summary>
    /// 수신 바이트 그대로(ANSI 이스케이프·NUL 포함). 재현·프로토콜 분석용 원본이며
    /// 사람이 읽기에는 지저분하다 — 그래서 기본이 아니다.
    /// </summary>
    Raw,
}

/// <summary>
/// 연속 로깅: 수신분을 도착하는 대로 파일에 기록한다(TeraTerm 의 Log 에 해당).
/// <see cref="LogFormat"/> 으로 <b>화면 그대로</b>(기본)와 <b>원시 바이트</b> 중 고른다.
///
/// 화면 버퍼(스크롤백)와 완전히 분리된 이유 — 스크롤백은 10,000줄/셀 총량 상한이 있어
/// 장시간 테스트에서는 앞부분이 이미 사라진 뒤다. "로그 저장" 은 그 잘린 스냅샷 1회 저장이고,
/// 이 클래스는 상한 없이 처음부터 끝까지 남긴다.
///
/// 설계 원칙:
///  · <b>쓰기는 RX 워커 스레드에서 동기로 일어난다.</b> 파일은 한 번 열어 두고(FileStream 버퍼링)
///    Append 끝에 1회 Flush 로 합쳐 써서 로컬 디스크에서는 µs 로 끝난다
///    (진단 로그가 호출마다 열고 닫아 1.1~1.6ms/건으로 측정된 전철을 밟지 않는다).
///    다만 <b>느린 매체에서는 수신을 막는다</b> — 네트워크 공유가 재연결로 수십 초 블록되면
///    그동안 RX 루프가 서고, 드라이버 버퍼(64KB)가 차면 그 뒤 바이트는 조용히 유실된다.
///    수신 경로에 이보다 무거운 것을 더 얹지 말 것. 이 위험을 없애려면 여기에 큐 + 전용
///    writer 스레드를 두어야 한다(현재는 안 되어 있다).
///  · 쓰기 실패는 로깅만 죽인다: 한 번 실패하면 <see cref="Failed"/> 를 알리고 이후 Append 는 무시.
///  · 타임스탬프를 켜면 각 줄 앞에 <c>[HH:mm:ss.fff] </c> 를 넣는다. 원시 모드에서 줄 경계는
///    CR/LF 이며 인접 쌍(CR+LF/LF+CR)은 한 번으로 본다 — 청크가 쌍 중간에서 잘려도
///    (수신은 임의 경계로 온다) 상태를 이어 가므로 이중 스탬프가 생기지 않는다.
///  · 화면 모드의 시각은 <b>줄이 끝난 시각</b>이다(그 줄이 화면에 완성된 때) — 첫 바이트 시각이
///    아니다. 한 줄이 여러 청크에 걸쳐 오는 것이 정상이라 '줄의 시각'을 하나만 고른다면 이쪽이 맞다.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly FileStream _file;
    private readonly Func<DateTime> _clock;

    private byte _lastBreak;        // 직전 바이트가 CR/LF 면 그 값(쌍 판정용), 아니면 0
    private bool _needStamp = true; // 다음 '내용' 바이트 앞에 스탬프를 넣어야 하는가
    private bool _dead;             // Dispose 됐거나 쓰기 실패 — 이후 Append 무시
    private long _bytesWritten;

    /// <summary>화면 모드에서 다음에 기록할 <b>절대</b> 라인 번호(-1 = 아직 기준을 못 잡음).</summary>
    private long _nextAbs = -1;

    /// <summary>화면 모드에서 스크롤백이 밀려 못 쓰고 놓친 줄 수(정상 사용에서는 0).</summary>
    private long _droppedLines;

    /// <summary>기록 중인 파일 경로.</summary>
    public string Path { get; }

    /// <summary>줄마다 수신 시각을 붙이는지(시작 시 고정).</summary>
    public bool Timestamps { get; }

    /// <summary>기록 형식(시작 시 고정).</summary>
    public LogFormat Format { get; }

    /// <summary>화면 모드에서 버퍼 상한에 밀려 기록하지 못한 줄 수.</summary>
    public long DroppedLines { get { lock (_sync) return _droppedLines; } }

    public long BytesWritten { get { lock (_sync) return _bytesWritten; } }

    /// <summary>
    /// 쓰기 실패 시 1회 발생(그 뒤 로깅은 스스로 멈춘다). <b>RX 워커 스레드에서 발생</b>하므로
    /// UI 를 만지는 구독자는 Dispatcher 로 마샬링해야 한다.
    /// </summary>
    public event Action<LocMessage>? Failed;

    /// <param name="append">true 면 기존 파일 끝에 이어 쓴다(TeraTerm 의 Append) — 여러 세션을 한 파일에 모을 때.</param>
    /// <param name="clock">테스트용 시계(기본 DateTime.Now).</param>
    /// <exception cref="IOException">파일을 만들 수 없을 때(호출자가 안내).</exception>
    public SessionLogger(string path, bool timestamps, bool append = false, Func<DateTime>? clock = null,
                         LogFormat format = LogFormat.Screen)
    {
        Path = path;
        Timestamps = timestamps;
        Format = format;
        _clock = clock ?? (() => DateTime.Now);
        // FileShare.Read — 기록 중에도 tail/편집기로 열어 볼 수 있게 한다.
        _file = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                               FileAccess.Write, FileShare.Read, 1 << 16);
    }

    /// <summary>
    /// 화면 버퍼 스냅샷을 기록(TeraTerm 의 "Include screen buffer"). 로깅 시작 직후 1회 호출.
    /// <b>줄별 타임스탬프는 붙이지 않는다</b> — 이 줄들의 실제 수신 시각은 알 수 없으므로
    /// '지금'을 찍으면 거짓 정보가 된다. 이후 수신분부터 정상적으로 스탬프가 붙는다.
    /// </summary>
    public void AppendScreenSnapshot(string text)
    {
        if (text.Length == 0) return;
        lock (_sync)
        {
            if (_dead) return;
            try
            {
                Write(Encoding.UTF8.GetBytes(text));
                if (!text.EndsWith('\n')) Write("\r\n"u8);
                _needStamp = true;   // 스냅샷 뒤 첫 수신 줄부터 스탬프
                _lastBreak = 0;
                _file.Flush();
            }
            catch (Exception ex)
            {
                _dead = true;
                try { _file.Dispose(); } catch { }
                try { Failed?.Invoke(LocMessage.Of("Log.Err.WriteFailed", ex.Message)); } catch { }
            }
        }
    }

    /// <summary>
    /// 수신 바이트 기록(시리얼 RX 워커 스레드). 실패/폐기 후에는 조용히 무시된다.
    /// <b>원시 모드에서만</b> 동작한다 — 화면 모드는 <see cref="AppendRenderedLines"/> 로 쓴다.
    /// (호출자가 형식을 따져 다른 훅을 걸 필요가 없도록 여기서 걸러낸다.)
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || Format != LogFormat.Raw) return;
        lock (_sync)
        {
            if (_dead) return;
            try
            {
                if (Timestamps) WriteStamped(data);
                else Write(data);
                _file.Flush();   // OS 페이지 캐시로만 밀어낸다(µs) — tail 가독성 + 비정상 종료 대비
            }
            catch (Exception ex)
            {
                _dead = true;
                try { _file.Dispose(); } catch { }
                try { Failed?.Invoke(LocMessage.Of("Log.Err.WriteFailed", ex.Message)); } catch { }
            }
        }
    }

    // ── 화면 모드 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 화면 모드의 기준점을 잡는다(로깅 시작 시 1회). 지금 <b>진행 중인 줄</b>부터 기록 대상이며,
    /// 그 줄은 끝날 때 <b>통째로</b> 쓰인다. '화면 버퍼 포함'을 쓸 때는 완성된 줄까지만 스냅샷으로
    /// 넣고 여기서 이어받아야 경계 줄이 두 번 적히지 않는다.
    /// </summary>
    public void StartAt(TerminalBuffer buffer)
    {
        lock (_sync) StartAtLocked(buffer);
    }

    /// <summary>
    /// 완성된 논리 라인을 파일로 흘려보낸다(시리얼 RX 워커 스레드에서 수신 처리 <b>직후</b> 호출).
    /// 마지막(진행 중) 줄은 <paramref name="flushPartial"/> 일 때만 쓴다 — 정지·문서 닫기에서 꼬리를 남기기 위한 것.
    ///
    /// 절대 라인 번호로 진행 위치를 잡기 때문에 스크롤백이 잘려도(<c>TrimmedCount</c> 증가)
    /// 같은 줄을 두 번 쓰거나 건너뛰지 않는다. 잘림이 우리보다 빠른 경우만 <see cref="DroppedLines"/> 로 센다.
    /// </summary>
    public void AppendRenderedLines(TerminalBuffer buffer, bool flushPartial = false)
    {
        if (Format != LogFormat.Screen) return;

        lock (_sync)
        {
            if (_dead) return;
            if (_nextAbs < 0) StartAtLocked(buffer);

            var lines = new List<string>();
            lock (buffer.SyncRoot)
            {
                long first = buffer.TrimmedCount;
                long endAbs = first + buffer.LineCount - (flushPartial ? 0 : 1);   // 끝은 배타적

                if (_nextAbs < first)
                {
                    _droppedLines += first - _nextAbs;   // 우리가 쓰기 전에 밀려 나갔다
                    _nextAbs = first;
                }

                for (long abs = _nextAbs; abs < endAbs; abs++)
                    lines.Add(buffer.GetLine((int)(abs - first)).Text());

                _nextAbs = Math.Max(_nextAbs, endAbs);
            }

            if (lines.Count == 0) return;

            try
            {
                foreach (string line in lines)
                {
                    if (Timestamps) WriteStamp();
                    Write(Encoding.UTF8.GetBytes(line));
                    Write("\r\n"u8);
                }
                _file.Flush();
            }
            catch (Exception ex)
            {
                _dead = true;
                try { _file.Dispose(); } catch { }
                try { Failed?.Invoke(LocMessage.Of("Log.Err.WriteFailed", ex.Message)); } catch { }
            }
        }
    }

    private void StartAtLocked(TerminalBuffer buffer)
    {
        lock (buffer.SyncRoot)
            _nextAbs = buffer.TrimmedCount + buffer.LineCount - 1;
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        _file.Write(data);
        _bytesWritten += data.Length;
    }

    /// <summary>
    /// 줄의 첫 내용 바이트 앞에 스탬프를 끼워 넣으며 기록한다. 개행 바이트 자체는 원본 그대로 남긴다
    /// (CR 덮어쓰기 진행률 표시도 각 세그먼트가 스탬프를 받는다 — 언제 갱신됐는지가 남는다).
    /// </summary>
    private void WriteStamped(ReadOnlySpan<byte> data)
    {
        int seg = 0;   // 아직 기록하지 않은 구간의 시작
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b is (byte)'\n' or (byte)'\r')
            {
                if (_lastBreak != 0 && b != _lastBreak)
                    _lastBreak = 0;          // CR+LF / LF+CR 쌍의 두 번째 — 같은 개행의 일부
                else
                {
                    _lastBreak = b;
                    _needStamp = true;       // 다음 내용 바이트가 새 줄의 시작
                }
                continue;
            }

            if (_needStamp)
            {
                Write(data[seg..i]);         // 여기까지(개행 포함) 내보내고
                seg = i;
                WriteStamp();                // 새 줄 앞에 시각
                _needStamp = false;
            }
            _lastBreak = 0;
        }
        Write(data[seg..]);
    }

    private void WriteStamp()
    {
        // ASCII 만 나온다 — 인코딩 모호성 없음
        Write(Encoding.ASCII.GetBytes($"[{_clock():HH:mm:ss.fff}] "));
    }

    /// <summary>정지/문서 닫힘. 이후 Append 는 무시된다(멱등).</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_dead) return;
            _dead = true;
            try { _file.Flush(); } catch { }
            try { _file.Dispose(); } catch { }
        }
    }
}
