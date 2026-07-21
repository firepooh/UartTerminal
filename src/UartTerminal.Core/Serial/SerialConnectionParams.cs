using System.IO.Ports;

namespace UartTerminal.Core.Serial;

/// <summary>
/// 시리얼 접속 파라미터. Phase A는 포트만 선택하고 나머지는 기본값 고정
/// (115200 / 8 / None / 1 / Flow None, DTR·RTS deassert = 보드 리셋 안 함).
/// </summary>
public sealed class SerialConnectionParams
{
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>오픈 시 DTR 상태. 기본 false — ESP32 IO0(부트 모드) 오작동 방지.</summary>
    public bool DtrEnable { get; init; }

    /// <summary>오픈 시 RTS 상태. 기본 false — ESP32 EN(리셋) 오작동 방지.</summary>
    public bool RtsEnable { get; init; }

    public static SerialConnectionParams Default => new();

    /// <summary>창 제목/상태바 표기용 요약 (예: 115200 8N1).</summary>
    public string Summary()
    {
        char p = Parity switch
        {
            Parity.None => 'N',
            Parity.Even => 'E',
            Parity.Odd => 'O',
            Parity.Mark => 'M',
            Parity.Space => 'S',
            _ => '?'
        };
        string stop = StopBits switch
        {
            StopBits.One => "1",
            StopBits.OnePointFive => "1.5",
            StopBits.Two => "2",
            _ => "?"
        };
        return $"{BaudRate} {DataBits}{p}{stop}";
    }
}
