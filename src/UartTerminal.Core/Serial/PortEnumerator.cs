using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace UartTerminal.Core.Serial;

/// <summary>
/// 사용 가능한 COM 포트 열거. <see cref="SerialPort.GetPortNames"/>는 이름만 주므로
/// WMI(Win32_PnPEntity)로 friendly name 을 조회해 합친다(README §5). WMI 실패 시 이름만 반환.
/// </summary>
public static partial class PortEnumerator
{
    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComInCaptionRegex();

    [GeneratedRegex(@"^COM(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComNumberRegex();

    public static IReadOnlyList<PortInfo> Enumerate()
    {
        var byName = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in SafeGetPortNames())
            byName[name] = new PortInfo(name, null);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Caption FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                // 각 ManagementBaseObject 는 부모 컬렉션과 별개로 해제해야 하는 COM 래퍼(핸들 누수 방지)
                using (mo)
                {
                    if (mo["Caption"] is not string caption)
                        continue;
                    var m = ComInCaptionRegex().Match(caption);
                    if (!m.Success)
                        continue;
                    string com = m.Groups[1].Value.ToUpperInvariant();
                    byName[com] = new PortInfo(com, caption);
                }
            }
        }
        catch
        {
            // WMI 미가용/오류 → GetPortNames 결과만 사용
        }

        return byName.Values
            .OrderBy(p => ComSortKey(p.PortName))
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>지정 포트명이 현재 존재하는지 빠르게 확인(WMI 없이 <see cref="SerialPort.GetPortNames"/>만). 자동 재연결 폴링용.</summary>
    public static bool PortExists(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return false;
        foreach (var n in SafeGetPortNames())
            if (string.Equals(n, portName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IEnumerable<string> SafeGetPortNames()
    {
        try
        {
            // GetPortNames 는 드물게 널 문자 등이 섞인 이름을 반환하므로 정리
            return SerialPort.GetPortNames()
                .Select(n => n.Trim().TrimEnd('\0'))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>COM2 &lt; COM10 이 되도록 숫자 기준 정렬 키.</summary>
    private static int ComSortKey(string portName)
    {
        var m = ComNumberRegex().Match(portName);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
    }
}
