using UartTerminal.Core.Serial;

namespace UartTerminal.Tests;

/// <summary>
/// 테스트용 가짜 시리얼 세션. 실제 포트 없이 <see cref="ISerialSession"/> 계약을 흉내내
/// 수신 데이터/종료 이벤트를 수동으로 발생시키고, 송신 바이트를 기록한다.
/// </summary>
internal sealed class FakeSerialSession : ISerialSession
{
    public string PortName { get; }
    public SerialConnectionParams Params { get; }
    public bool IsOpen { get; private set; }
    public bool DtrEnabled { get; private set; }
    public bool RtsEnabled { get; private set; }

    /// <summary>Enqueue 로 들어온 송신 바이트(검증용).</summary>
    public List<byte[]> Sent { get; } = new();

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<SerialCloseReason>? Closed;

    public FakeSerialSession(string port = "COM9", int baud = 115200)
    {
        PortName = port;
        Params = new SerialConnectionParams { BaudRate = baud };
    }

    public void Open() => IsOpen = true;

    public void Enqueue(ReadOnlyMemory<byte> data) => Sent.Add(data.ToArray());

    public void SetDtrRts(bool dtr, bool rts) { DtrEnabled = dtr; RtsEnabled = rts; }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Closed?.Invoke(SerialCloseReason.UserClosed);
    }

    public void Dispose() => IsOpen = false;

    // ── 테스트 헬퍼 ──────────────────────────────────────────────────────────
    public void EmitData(byte[] data) => DataReceived?.Invoke(data);

    public void SimulateClosed(SerialCloseReason reason)
    {
        IsOpen = false;
        Closed?.Invoke(reason);
    }
}
