namespace UartTerminal.Core.Serial;

/// <summary>세션 종료 사유.</summary>
public enum SerialCloseReason
{
    /// <summary>사용자가 직접 닫음.</summary>
    UserClosed,
    /// <summary>장치 제거(USB 분리 등).</summary>
    DeviceRemoved,
    /// <summary>기타 오류.</summary>
    Error
}

/// <summary>
/// 시리얼 세션 추상화(README §4.2). 구현을 교체(RJCP 등)하거나 테스트에서 fake 를 주입할 수 있도록
/// 인터페이스 뒤에 시리얼 I/O를 격리한다. 모든 TX는 단일 큐로 직렬화되어 순서/원자성이 보장된다.
/// 이벤트는 내부 워커 스레드에서 발생하므로 UI 반영 시 마샬링이 필요하다.
/// </summary>
public interface ISerialSession : IDisposable
{
    string PortName { get; }
    SerialConnectionParams Params { get; }
    bool IsOpen { get; }

    /// <summary>현재 DTR 출력 상태(마지막으로 설정된 값). 상태 조회/표시용.</summary>
    bool DtrEnabled { get; }

    /// <summary>현재 RTS 출력 상태(마지막으로 설정된 값). RTS/CTS 흐름제어 시 하드웨어가 제어하므로 의미가 제한적.</summary>
    bool RtsEnabled { get; }

    /// <summary>원시 수신 바이트(tee 지점: 화면 엔진 + MCP 링버퍼가 구독).</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>세션 종료(사용자 종료/장치 제거/오류).</summary>
    event Action<SerialCloseReason>? Closed;

    void Open();

    /// <summary>단일 TX 큐에 송신 데이터 적재(키 입력/붙여넣기/AI 전송 공통 경로).</summary>
    void Enqueue(ReadOnlyMemory<byte> data);

    /// <summary>
    /// DTR/RTS 제어선을 설정한다(MCP <c>uart_set_dtr_rts</c> — 예: ESP32 부트로더 진입/리셋 시퀀스).
    /// RTS/CTS 흐름제어가 켜진 경우 RTS 설정은 하드웨어에 의해 무시될 수 있다. 닫힌 세션에서는 무시된다.
    /// </summary>
    void SetDtrRts(bool dtr, bool rts);

    void Close();
}
