using System.Text;

namespace UartTerminal.Core.Terminal;

/// <summary>
/// 상태 유지 증분 디코더. 시리얼 수신은 임의 지점에서 청크가 끊기므로 멀티바이트 문자
/// (UTF-8 한글 3바이트 등)가 read 경계에서 잘려 도착할 수 있다. <see cref="Decoder"/>는
/// 잔여 바이트를 내부에 보존하므로, 청크마다 GetString을 호출할 때 생기는 문자 깨짐을 방지한다.
/// (README §4.2 / R8) UTF-8은 ASCII-투명이고 연속 바이트에 ESC(0x1B)가 없어 "디코드 후 파싱"이 안전하다.
/// </summary>
public sealed class StreamDecoder
{
    private readonly Decoder _decoder;
    private readonly char[] _chars;

    public StreamDecoder(Encoding encoding, int bufferChars = 8192)
    {
        _decoder = encoding.GetDecoder();
        _chars = new char[Math.Max(256, bufferChars)];
    }

    /// <summary>인코딩 전환/재연결 시 내부 잔여 바이트 상태를 초기화.</summary>
    public void Reset() => _decoder.Reset();

    /// <summary>수신 바이트를 디코드해 파서로 흘려보낸다.</summary>
    public void Decode(ReadOnlySpan<byte> bytes, AnsiParser parser)
    {
        var remaining = bytes;
        while (true)
        {
            _decoder.Convert(remaining, _chars, flush: false,
                out int bytesUsed, out int charsProduced, out _);

            if (charsProduced > 0)
                parser.Feed(_chars.AsSpan(0, charsProduced));

            remaining = remaining[bytesUsed..];
            if (remaining.IsEmpty)
                break;
            if (bytesUsed == 0 && charsProduced == 0)
                break; // 진전 없음(불완전 후행 바이트는 디코더가 내부 보존) — 방어적 종료
        }
    }
}
