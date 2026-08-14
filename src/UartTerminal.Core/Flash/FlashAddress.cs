using System.Globalization;

namespace UartTerminal.Core.Flash;

/// <summary>
/// 사용자가 입력한 플래시 주소 파싱. "0xD90000"(hex) 또는 십진 문자열을 받는다.
/// 잘못된 주소로 구우면 장비가 손상되므로 <b>관대한 추측 없이</b> 정확히 파싱되는 것만 통과시킨다.
/// </summary>
public static class FlashAddress
{
    public static bool TryParse(string? s, out uint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
            : uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
    }
}
