using System.Text;

namespace UartTerminal.Core.Terminal;

/// <summary>
/// 수신 바이트를 화면 상태로 바꾸는 파이프라인: 바이트 → 증분 디코더 → ANSI 파서 → 논리 라인 버퍼.
/// <see cref="Receive"/>는 시리얼 워커 스레드에서 호출되며, 처리 구간 전체를 버퍼 락으로 감싸
/// 렌더러(UI 스레드)에 일관된 스냅샷을 보장한다(README §4.2).
/// </summary>
public sealed class TerminalEngine
{
    private readonly StreamDecoder _decoder;
    private readonly AnsiParser _parser;

    public TerminalBuffer Buffer { get; }

    /// <summary>DSR 등 터미널→호스트 응답 바이트를 시리얼 TX로 연결하는 콜백.</summary>
    public Action<ReadOnlyMemory<byte>>? Respond
    {
        get => _parser.Respond;
        set => _parser.Respond = value;
    }

    public TerminalEngine(Encoding? encoding = null, int maxLines = 10_000)
    {
        Buffer = new TerminalBuffer(maxLines);
        _decoder = new StreamDecoder(encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _parser = new AnsiParser(Buffer);
    }

    public long Revision => Buffer.Revision;

    /// <summary>수신 바이트를 처리(디코드→파싱→버퍼 반영). 시리얼 워커 스레드에서 호출.</summary>
    public void Receive(ReadOnlySpan<byte> bytes)
    {
        lock (Buffer.SyncRoot)
        {
            _decoder.Decode(bytes, _parser);
        }
    }

    /// <summary>재연결/인코딩 전환 시 디코더·파서 상태 초기화(버퍼 내용은 유지).</summary>
    public void ResetParsing()
    {
        lock (Buffer.SyncRoot)
        {
            _decoder.Reset();
            _parser.Reset();
        }
    }
}
