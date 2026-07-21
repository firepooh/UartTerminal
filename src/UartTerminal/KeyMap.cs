using System.Windows.Input;

namespace UartTerminal;

/// <summary>
/// type-through 입력 키맵(README §6 Q1). 특수 키를 송신 바이트로 변환한다.
/// 일반 문자(글자/숫자/한글 등)는 여기서 null 을 반환하고 TextInput 경로에서 처리한다.
/// 화살표=ESC[A~D(linenoise 히스토리/커서), Backspace=0x7F.
/// </summary>
public static class KeyMap
{
    private const byte ESC = 0x1B;

    public static byte[]? Map(Key key, ModifierKeys mods)
    {
        bool ctrlOnly = mods == ModifierKeys.Control;

        // Ctrl+[A-Z] → 제어 코드(0x01~0x1A). Ctrl+C=0x03 (복사가 아니라 인터럽트 전송).
        if (ctrlOnly && key >= Key.A && key <= Key.Z)
            return new[] { (byte)(key - Key.A + 1) };

        switch (key)
        {
            case Key.Enter: return new byte[] { 0x0D };      // New-line Transmit = CR
            case Key.Back: return new byte[] { 0x7F };       // Backspace = DEL
            case Key.Tab: return new byte[] { 0x09 };
            case Key.Escape: return new byte[] { ESC };
            case Key.Up: return new byte[] { ESC, (byte)'[', (byte)'A' };
            case Key.Down: return new byte[] { ESC, (byte)'[', (byte)'B' };
            case Key.Right: return new byte[] { ESC, (byte)'[', (byte)'C' };
            case Key.Left: return new byte[] { ESC, (byte)'[', (byte)'D' };
            case Key.Home: return new byte[] { ESC, (byte)'[', (byte)'H' };
            case Key.End: return new byte[] { ESC, (byte)'[', (byte)'F' };
            case Key.Delete: return new byte[] { ESC, (byte)'[', (byte)'3', (byte)'~' };
            default: return null;
        }
    }
}
