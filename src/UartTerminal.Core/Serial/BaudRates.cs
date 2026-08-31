using System.Globalization;

namespace UartTerminal.Core.Serial;

/// <summary>
/// 고를 수 있는 통신 속도. 접속 다이얼로그와 세션 관리가 각자 목록을 들고 있었는데,
/// 한쪽만 늘리면 같은 값을 세션에는 저장할 수 있고 접속할 때는 못 고르는 일이 생겨 한곳으로 모았다.
///
/// <para><b>목록은 편의일 뿐 제한이 아니다.</b> 두 화면 모두 직접 입력을 받는다 —
/// 장비가 쓰는 속도는 표준 목록 밖에도 있고(ESP32 는 수 Mbps 까지), 못 적게 막아서 얻는 것이 없다.</para>
/// </summary>
public static class BaudRates
{
    /// <summary>
    /// 표준 속도에 <b>74880</b> 을 끼운 목록. 74880 은 ESP32 ROM 부트로더의 출력 속도로,
    /// 부트루프를 진단할 때 이 값이 아니면 글자가 깨져 보인다 — 표준은 아니지만 이 앱에서는 실수요다.
    /// </summary>
    public static readonly int[] Common =
    {
        110, 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600,
        74880, 115200, 230400, 460800, 921600,
    };

    public const int Default = 115200;

    /// <summary>
    /// 사용자가 친 속도를 읽는다. 자릿수 구분 쉼표(<c>115,200</c>)와 앞뒤 공백을 받아 준다 —
    /// 목록에서 복사해 붙이는 흔한 경로다. 0 이하는 거절한다(SerialPort 가 예외를 던진다).
    /// </summary>
    public static bool TryParse(string? text, out int baud)
    {
        baud = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim().Replace(",", "").Replace("_", "");
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int v)) return false;
        if (v <= 0) return false;

        baud = v;
        return true;
    }
}
