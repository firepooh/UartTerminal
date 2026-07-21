namespace UartTerminal.Core.Serial;

/// <summary>포트 목록 항목. 사용자가 어느 보드인지 구분할 수 있도록 friendly name 을 함께 표시.</summary>
public sealed record PortInfo(string PortName, string? FriendlyName)
{
    /// <summary>UI 표시용 문자열. 예: "COM4 — Silicon Labs CP210x USB to UART Bridge".</summary>
    public string Display =>
        string.IsNullOrEmpty(FriendlyName) ? PortName : $"{PortName} — {StripComSuffix(FriendlyName!)}";

    private static string StripComSuffix(string caption)
    {
        // "Silicon Labs CP210x USB to UART Bridge (COM4)" → "Silicon Labs CP210x USB to UART Bridge"
        int idx = caption.LastIndexOf(" (COM", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? caption[..idx] : caption;
    }
}
