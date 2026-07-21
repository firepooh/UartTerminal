namespace UartTerminal.Core.Terminal;

/// <summary>색상 표현 방식.</summary>
public enum ColorKind : byte
{
    /// <summary>터미널 기본 전경/배경색(렌더러가 결정).</summary>
    Default,
    /// <summary>팔레트 인덱스(0~15: SGR 16색+bright. 향후 256색 확장 가능).</summary>
    Palette,
    /// <summary>직접 RGB 지정(트루컬러, 향후 확장).</summary>
    Rgb
}

/// <summary>
/// 셀 색상. 논리 모델은 인덱스/기본값만 저장하고, 실제 RGB는 렌더러의 팔레트가 결정한다.
/// </summary>
public readonly struct TermColor : IEquatable<TermColor>
{
    public readonly ColorKind Kind;
    public readonly byte Index;   // Palette
    public readonly byte R, G, B; // Rgb

    private TermColor(ColorKind kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind; Index = index; R = r; G = g; B = b;
    }

    public static readonly TermColor Default = new(ColorKind.Default, 0, 0, 0, 0);

    public static TermColor FromPalette(int index) =>
        new(ColorKind.Palette, (byte)(index & 0xFF), 0, 0, 0);

    public static TermColor FromRgb(byte r, byte g, byte b) =>
        new(ColorKind.Rgb, 0, r, g, b);

    public bool Equals(TermColor other) =>
        Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is TermColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine((byte)Kind, Index, R, G, B);
    public static bool operator ==(TermColor a, TermColor b) => a.Equals(b);
    public static bool operator !=(TermColor a, TermColor b) => !a.Equals(b);
}

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Reverse = 1 << 4
}

/// <summary>한 셀(문자)의 시각 속성. SGR 파싱 결과가 여기에 누적 반영된다.</summary>
public readonly struct CellAttributes : IEquatable<CellAttributes>
{
    public readonly TermColor Foreground;
    public readonly TermColor Background;
    public readonly CellFlags Flags;

    public CellAttributes(TermColor foreground, TermColor background, CellFlags flags)
    {
        Foreground = foreground;
        Background = background;
        Flags = flags;
    }

    public static readonly CellAttributes Default =
        new(TermColor.Default, TermColor.Default, CellFlags.None);

    public CellAttributes WithForeground(TermColor fg) => new(fg, Background, Flags);
    public CellAttributes WithBackground(TermColor bg) => new(Foreground, bg, Flags);
    public CellAttributes WithFlags(CellFlags flags) => new(Foreground, Background, flags);
    public CellAttributes AddFlag(CellFlags f) => new(Foreground, Background, Flags | f);
    public CellAttributes RemoveFlag(CellFlags f) => new(Foreground, Background, Flags & ~f);

    public bool Equals(CellAttributes other) =>
        Foreground == other.Foreground && Background == other.Background && Flags == other.Flags;

    public override bool Equals(object? obj) => obj is CellAttributes c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(Foreground, Background, (byte)Flags);
    public static bool operator ==(CellAttributes a, CellAttributes b) => a.Equals(b);
    public static bool operator !=(CellAttributes a, CellAttributes b) => !a.Equals(b);
}

/// <summary>
/// 논리 라인을 구성하는 한 셀: 문자 하나 + 그 문자의 시각 속성.
/// Phase A는 BMP 문자(ASCII+한글) 대상이며 각 셀은 UTF-16 코드 유닛 하나를 담는다.
/// </summary>
public readonly struct Cell
{
    public readonly char Ch;
    public readonly CellAttributes Attributes;

    public Cell(char ch, CellAttributes attributes)
    {
        Ch = ch;
        Attributes = attributes;
    }
}
