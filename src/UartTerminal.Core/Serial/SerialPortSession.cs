using System.IO.Ports;
using System.Threading.Channels;

namespace UartTerminal.Core.Serial;

/// <summary>
/// System.IO.Ports 기반 시리얼 세션. README §4.2 / R1 방침:
///  - <see cref="SerialPort.DataReceived"/> 이벤트를 쓰지 않고 <c>BaseStream.ReadAsync</c> 전용 워커 루프 사용
///    (스레드풀 마샬링/Close 데드락/예외 삼킴 회피).
///  - USB 분리 시 발생하는 예외(ObjectDisposed/IOException/UnauthorizedAccess)를 "종료" 이벤트로 정규화해
///    프로세스가 죽지 않게 한다.
///  - 포트 Dispose 는 별도 스레드에서 타임아웃과 함께 수행(분리 후 Close 가 블록/예외 나는 문제 대비).
/// </summary>
public sealed class SerialPortSession : ISerialSession
{
    private readonly SerialPort _port;
    private readonly Channel<byte[]> _txQueue;
    private CancellationTokenSource? _cts;
    private Task? _rxTask;
    private Task? _txTask;
    private int _closedFlag; // Interlocked: 0=열림, 1=종료됨

    public string PortName { get; }
    public SerialConnectionParams Params { get; }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<SerialCloseReason>? Closed;

    public SerialPortSession(string portName, SerialConnectionParams parameters)
    {
        PortName = portName;
        Params = parameters;
        _txQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _port = new SerialPort(portName, parameters.BaudRate, parameters.Parity,
            parameters.DataBits, parameters.StopBits)
        {
            Handshake = parameters.Handshake,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout,
            ReadBufferSize = 1 << 16,
            WriteBufferSize = 1 << 16
        };
    }

    public bool IsOpen
    {
        get
        {
            if (Volatile.Read(ref _closedFlag) != 0) return false;
            try { return _port.IsOpen; }
            catch { return false; }
        }
    }

    public void Open()
    {
        // DTR/RTS 초기 상태를 오픈 전에 설정해 오픈 순간의 보드 리셋을 방지.
        _port.DtrEnable = Params.DtrEnable;
        // RTS/CTS 흐름제어일 때 RtsEnable 접근은 InvalidOperationException(README R5) → 그 경우 건너뜀.
        if (Params.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
        {
            try { _port.RtsEnable = Params.RtsEnable; } catch (InvalidOperationException) { }
        }

        _port.Open();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _rxTask = Task.Run(() => RxLoopAsync(ct));
        _txTask = Task.Run(() => TxLoopAsync(ct));
    }

    public void Enqueue(ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _closedFlag) != 0 || data.IsEmpty) return;
        _txQueue.Writer.TryWrite(data.ToArray());
    }

    public void Close() => Shutdown(SerialCloseReason.UserClosed);

    public void Dispose() => Shutdown(SerialCloseReason.UserClosed);

    private async Task RxLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        try
        {
            var stream = _port.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // 정상 종료
                }
                catch (Exception ex) when (IsDisconnect(ex))
                {
                    Shutdown(SerialCloseReason.DeviceRemoved);
                    return;
                }

                if (n <= 0)
                {
                    if (!ct.IsCancellationRequested)
                        Shutdown(SerialCloseReason.DeviceRemoved);
                    return;
                }

                var chunk = new byte[n];
                Array.Copy(buffer, chunk, n);
                try { DataReceived?.Invoke(chunk); }
                catch { /* 구독자 예외 격리 */ }
            }
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            Shutdown(SerialCloseReason.DeviceRemoved);
        }
    }

    private async Task TxLoopAsync(CancellationToken ct)
    {
        try
        {
            var stream = _port.BaseStream;
            await foreach (var data in _txQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await stream.WriteAsync(data.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (IsDisconnect(ex))
                {
                    Shutdown(SerialCloseReason.DeviceRemoved);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private static bool IsDisconnect(Exception ex) =>
        ex is IOException
           or ObjectDisposedException
           or UnauthorizedAccessException
           or InvalidOperationException;

    private void Shutdown(SerialCloseReason reason)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
            return; // 한 번만

        try { _cts?.Cancel(); } catch { }
        _txQueue.Writer.TryComplete();
        DisposePortSafely();

        try { Closed?.Invoke(reason); }
        catch { }
    }

    /// <summary>포트 Dispose 를 별도 스레드에서 수행하고 짧게 대기(분리 후 Dispose 블록/예외 대비).</summary>
    private void DisposePortSafely()
    {
        var port = _port;
        var t = new Thread(() =>
        {
            try { port.Dispose(); }
            catch { }
        })
        { IsBackground = true, Name = "SerialDispose" };
        t.Start();
        t.Join(TimeSpan.FromMilliseconds(1500));
    }
}
