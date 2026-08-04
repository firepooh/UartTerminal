namespace UartTerminal.Core.Serial;

/// <summary>제어선 한 단계: DTR/RTS 를 이 값으로 두고 <see cref="DelayMs"/> 만큼 유지한다.</summary>
public readonly record struct ControlLineStep(bool Dtr, bool Rts, int DelayMs);

/// <summary>
/// ESP32 개발보드의 자동 프로그램 회로(DTR→IO0, RTS→EN, 트랜지스터 2개) 기준 리셋/부트로더 시퀀스.
/// esptool 의 "classic reset" 과 같은 순서·타이밍이다.
///
/// <b>극성 주의</b>: .NET <c>SerialPort.DtrEnable = true</c> 는 DTR 을 <i>assert</i>(핀 전압 LOW) 한다.
/// 보드 회로도의 진리표는 핀 전압 기준(1=HIGH)이므로 표의 1 이 Enable=false 에 대응한다:
/// <code>
///   Enable(dtr:false, rts:true)  → 핀(DTR=1, RTS=0) → EN=0, IO0=1   리셋 걸림
///   Enable(dtr:true,  rts:false) → 핀(DTR=0, RTS=1) → EN=1, IO0=0   리셋 해제 + 부트로더 진입
///   Enable(false,false) / (true,true)                → EN=1, IO0=1   정상 실행
/// </code>
/// 두 트랜지스터가 교차 결합돼 있어 (0,0) 과 (1,1) 이 같은 결과(정상 실행)라는 점이 회로의 핵심이다.
/// </summary>
public static class EspResetSequence
{
    /// <summary>EN(리셋)을 낮게 유지하는 시간(ms). esptool 기본값과 동일.</summary>
    public const int AssertMs = 100;

    /// <summary>리셋 해제 후 부트 모드가 래치될 때까지 대기(ms).</summary>
    public const int SettleMs = 50;

    /// <summary>하드웨어 리셋(EN 펄스). IO0 은 HIGH 로 유지 → 평소처럼 펌웨어가 부팅된다.</summary>
    public static readonly ControlLineStep[] HardReset =
    {
        new(Dtr: false, Rts: true,  DelayMs: AssertMs), // EN=0 (리셋), IO0=1
        new(Dtr: false, Rts: false, DelayMs: SettleMs), // EN=1 (실행), IO0=1
    };

    /// <summary>부트로더(다운로드 모드) 진입: 리셋을 걸고 IO0=LOW 상태로 풀어준다.</summary>
    public static readonly ControlLineStep[] Bootloader =
    {
        new(Dtr: false, Rts: true,  DelayMs: AssertMs), // EN=0 (리셋), IO0=1
        new(Dtr: true,  Rts: false, DelayMs: SettleMs), // EN=1 (실행), IO0=0 → 부트로더로 진입
        new(Dtr: false, Rts: false, DelayMs: 0),        // IO0 복귀(이미 부트 모드가 래치된 뒤)
    };

    /// <summary>
    /// 시퀀스를 동기 실행한다(단계마다 <paramref name="sleep"/> 만큼 블록).
    /// 총 소요 시간은 <see cref="AssertMs"/>+<see cref="SettleMs"/> ≈ 150ms 이므로 UI 스레드에서 쓸 때 주의.
    /// </summary>
    public static void Apply(ISerialSession session, IReadOnlyList<ControlLineStep> steps,
                             Action<int>? sleep = null)
    {
        sleep ??= ms => { if (ms > 0) Thread.Sleep(ms); };
        foreach (var step in steps)
        {
            if (!session.IsOpen) return; // 도중에 분리/닫힘 → 조용히 중단
            session.SetDtrRts(step.Dtr, step.Rts);
            sleep(step.DelayMs);
        }
    }

    /// <summary>시퀀스를 비동기 실행한다(UI 를 막지 않는 경로: 메뉴/단축키/MCP).</summary>
    public static async Task ApplyAsync(ISerialSession session, IReadOnlyList<ControlLineStep> steps,
                                        CancellationToken ct = default)
    {
        foreach (var step in steps)
        {
            if (!session.IsOpen) return;
            session.SetDtrRts(step.Dtr, step.Rts);
            if (step.DelayMs > 0)
                await Task.Delay(step.DelayMs, ct).ConfigureAwait(false);
        }
    }
}
