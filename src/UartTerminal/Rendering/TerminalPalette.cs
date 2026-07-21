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

    public Color DefaultForeground { get; init; } = Color.FromRgb(0xD4, 0xD4, 0xD4);
    public Color DefaultBackground { get; init; } = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public Color SelectionBackground { get; init; } = Color.FromArgb(0x80, 0x26, 0x4F, 0x78);
    public Color CursorColor { get; init; } = Color.FromRgb(0xD4, 0xD4, 0xD4);

    public static TerminalPalette Dark { get; } = new();

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
