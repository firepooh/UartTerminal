using System.Windows;
using System.Windows.Media;

namespace UartTerminal.Rendering;

/// <summary>
/// 고정폭 셀 그리드 렌더링에 필요한 폰트 메트릭. 기본 typeface 와 한글 폴백 typeface 를 함께 보유해
/// ASCII 는 기본 폰트, 한글 등 기본 폰트에 없는 글리프는 폴백 폰트로 그린다.
/// 셀 폭/높이는 정수 DIP 로 스냅해 열 정렬과 선명도를 확보한다.
/// </summary>
public sealed class FontMetrics
{
    public GlyphTypeface Primary { get; }
    public GlyphTypeface? Fallback { get; }
    public double FontSize { get; }
    public double CellWidth { get; }
    public double CellHeight { get; }
    public double BaselineY { get; }
    public float PixelsPerDip { get; }

    private FontMetrics(GlyphTypeface primary, GlyphTypeface? fallback, double fontSize, float pixelsPerDip)
    {
        Primary = primary;
        Fallback = fallback;
        FontSize = fontSize;
        PixelsPerDip = pixelsPerDip;

        // '0' 글리프의 advance 를 셀 폭으로(고정폭 폰트에서 모든 ASCII 동일)
        double advance = 0.6; // fallback 비율
        if (primary.CharacterToGlyphMap.TryGetValue('0', out ushort gi))
            advance = primary.AdvanceWidths[gi];
        CellWidth = Math.Max(1, Math.Round(advance * fontSize));
        CellHeight = Math.Max(1, Math.Round(primary.Height * fontSize));
        BaselineY = Math.Round(primary.Baseline * fontSize);
    }

    public static FontMetrics? Create(IEnumerable<string> primaryPreference,
                                      IEnumerable<string> fallbackPreference,
                                      double fontSize, float pixelsPerDip)
    {
        var primary = ResolveGlyphTypeface(primaryPreference)
                      ?? ResolveGlyphTypeface(new[] { "Consolas", "Courier New", "Lucida Console" })
                      ?? ResolveAnyMonospace();
        if (primary is null)
            return null; // 어떤 폰트도 해석 못 함 → 호출자가 안전 처리(크래시 방지)

        var fallback = ResolveGlyphTypeface(fallbackPreference);
        return new FontMetrics(primary, fallback, fontSize, pixelsPerDip);
    }

    /// <summary>지정 폰트가 모두 실패할 때 시스템 폰트 중 '0' 글리프가 있는 아무 typeface 를 최후 폴백으로 확보.</summary>
    private static GlyphTypeface? ResolveAnyMonospace()
    {
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                var tf = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (tf.TryGetGlyphTypeface(out var g) && g.CharacterToGlyphMap.ContainsKey('0'))
                    return g;
            }
        }
        catch { }
        return null;
    }

    private static readonly HashSet<string> InstalledFamilies = LoadInstalledFamilies();

    private static HashSet<string> LoadInstalledFamilies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Fonts.SystemFontFamilies)
            {
                foreach (var name in f.FamilyNames.Values)
                    set.Add(name);
                if (!string.IsNullOrEmpty(f.Source))
                    set.Add(f.Source);
            }
        }
        catch { /* 폰트 열거 실패 시 빈 집합 */ }
        return set;
    }

    private static GlyphTypeface? ResolveGlyphTypeface(IEnumerable<string> preference)
    {
        foreach (var family in preference)
        {
            if (!InstalledFamilies.Contains(family))
                continue;
            var tf = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out var gtf))
                return gtf;
        }
        return null;
    }

    /// <summary>코드포인트의 글리프 인덱스를 기본→폴백 순으로 조회. 반환 typeface 로 GlyphRun 을 구성한다.</summary>
    public bool TryGetGlyph(int codePoint, out GlyphTypeface typeface, out ushort glyphIndex)
    {
        if (Primary.CharacterToGlyphMap.TryGetValue(codePoint, out glyphIndex) && glyphIndex != 0)
        {
            typeface = Primary;
            return true;
        }
        if (Fallback is not null && Fallback.CharacterToGlyphMap.TryGetValue(codePoint, out glyphIndex) && glyphIndex != 0)
        {
            typeface = Fallback;
            return true;
        }
        // 미지원 글리프: 기본 typeface 의 .notdef(0) 로라도 자리 유지
        typeface = Primary;
        glyphIndex = 0;
        return false;
    }
}
