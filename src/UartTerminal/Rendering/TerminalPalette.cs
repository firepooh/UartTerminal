using System.Windows.Media;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Rendering;

/// <summary>
/// SGR 16색 팔레트와 기본 전경/배경색을 실제 <see cref="Color"/>로 해석한다.
/// 논리 모델(<see cref="TermColor"/>)은 인덱스/기본값만 저장하고 실제 RGB는 여기서 결정한다.
/// </summary>
public sealed class TerminalPalette
{
    // xterm 계열 표준 16색
    private static readonly Color[] Ansi16 =
    {
        Color.FromRgb(0x00, 0x00, 0x00), // 0 black
        Color.FromRgb(0xCD, 0x00, 0x00), // 1 red
        Color.FromRgb(0x00, 0xCD, 0x00), // 2 green
        Color.FromRgb(0xCD, 0xCD, 0x00), // 3 yellow
        Color.FromRgb(0x24, 0x72, 0xC8), // 4 blue (약간 밝게: 가독성)
        Color.FromRgb(0xCD, 0x00, 0xCD), // 5 magenta
        Color.FromRgb(0x00, 0xCD, 0xCD), // 6 cyan
        Color.FromRgb(0xE5, 0xE5, 0xE5), // 7 white
        Color.FromRgb(0x7F, 0x7F, 0x7F), // 8 bright black (gray)
        Color.FromRgb(0xFF, 0x00, 0x00), // 9 bright red
        Color.FromRgb(0x00, 0xFF, 0x00), // 10 bright green
        Color.FromRgb(0xFF, 0xFF, 0x00), // 11 bright yellow
        Color.FromRgb(0x5C, 0x5C, 0xFF), // 12 bright blue
        Color.FromRgb(0xFF, 0x00, 0xFF), // 13 bright magenta
        Color.FromRgb(0x00, 0xFF, 0xFF), // 14 bright cyan
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 15 bright white
    };

    // 기본색은 테마에서 읽는다(코드에 복사해 두면 테마를 바꿔도 화면이 안 따라온다).
    // 테마가 없을 때(단위 테스트 등)는 GitHub-dark 값으로 떨어진다.
    public Color DefaultForeground { get; init; } = Theme.ColorOr("C.TermFg", Color.FromRgb(0xE6, 0xED, 0xF5));
    public Color DefaultBackground { get; init; } = Theme.ColorOr("C.TermBg", Color.FromRgb(0x0D, 0x11, 0x17));
    public Color SelectionBackground { get; init; } = Color.FromArgb(0x66, 0x2F, 0x81, 0xF7);
    public Color CursorColor { get; init; } = Theme.ColorOr("C.TermCursor", Color.FromRgb(0x3F, 0xB9, 0x50));

    /// <summary>현재 테마 기준 팔레트. 테마를 바꾸면 다시 만들어야 한다(<see cref="Reload"/>).</summary>
    public static TerminalPalette Dark { get; private set; } = new();

    /// <summary>테마 전환 후 기본색을 다시 읽는다.</summary>
    public static void Reload() => Dark = new TerminalPalette();

    /// <summary>전경색 해석. bold 이고 팔레트 0~7이면 bright(8~15)로 승격(일반 터미널 동작).</summary>
    public Color ResolveForeground(in CellAttributes attr)
    {
        var c = attr.Foreground;
        return c.Kind switch
        {
            ColorKind.Default => DefaultForeground,
            ColorKind.Rgb => Color.FromRgb(c.R, c.G, c.B),
            ColorKind.Palette => ResolvePalette(c.Index, brightenIfBold: attr.Flags.HasFlag(CellFlags.Bold)),
            _ => DefaultForeground
        };
    }

    public Color ResolveBackground(in CellAttributes attr)
    {
        var c = attr.Background;
        return c.Kind switch
        {
            ColorKind.Default => DefaultBackground,
            ColorKind.Rgb => Color.FromRgb(c.R, c.G, c.B),
            ColorKind.Palette => ResolvePalette(c.Index, brightenIfBold: false),
            _ => DefaultBackground
        };
    }

    /// <summary>배경이 기본색이 아닌지(배경 rect를 그려야 하는지).</summary>
    public bool HasExplicitBackground(in CellAttributes attr) =>
        attr.Background.Kind != ColorKind.Default || attr.Flags.HasFlag(CellFlags.Reverse);

    private Color ResolvePalette(int index, bool brightenIfBold)
    {
        if (brightenIfBold && index < 8)
            index += 8;
        if (index >= 0 && index < Ansi16.Length)
            return Ansi16[index];
        // 256색/미지원 인덱스 근사(향후 256색 확장 여지): 기본 전경색으로.
        return DefaultForeground;
    }
}
