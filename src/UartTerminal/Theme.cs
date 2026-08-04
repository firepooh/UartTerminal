using System.Windows;
using System.Windows.Media;

namespace UartTerminal;

/// <summary>
/// 테마 리소스 접근 창구. <b>색은 테마 사전(DarkTheme.xaml 등)에만 두고</b> 코드는 여기로만 읽는다.
///
/// 예전에는 셸·렌더러가 같은 팔레트를 코드에 복사해 갖고 있었고, 그래서 테마의 색을 고쳐도
/// 패널 경계·상태 점·선택 영역은 옛 색을 그대로 써서 화면이 어긋났다(시인성 문제의 절반이 이것이었다).
/// 리소스가 없을 때는 눈에 띄는 대체색(마젠타)을 돌려주어 <b>누락을 조용히 넘기지 않는다</b>.
/// </summary>
public static class Theme
{
    private static readonly Brush Missing = MakeFrozen(Colors.Magenta);
    private static readonly Color MissingColor = Colors.Magenta;

    /// <summary>테마 브러시. 키가 없으면 마젠타(개발 중 즉시 눈에 띄게).</summary>
    public static Brush Brush(string key)
    {
        object? res = Find(key);
        if (res is Brush b) return b;
        if (res is Color c) return MakeFrozen(c);
        DiagLog.Warn($"테마 브러시 없음: {key}");
        return Missing;
    }

    /// <summary>테마 색. 렌더러(GlyphRun/Pen)는 Color 가 필요하다.</summary>
    public static Color Color(string key)
    {
        object? res = Find(key);
        if (res is Color c) return c;
        if (res is SolidColorBrush sb) return sb.Color;
        DiagLog.Warn($"테마 색 없음: {key}");
        return MissingColor;
    }

    /// <summary>키가 있으면 그 색, 없으면 <paramref name="fallback"/>(경고 없이). 선택적 확장 키에 쓴다.</summary>
    public static Color ColorOr(string key, Color fallback)
    {
        object? res = Find(key);
        return res switch
        {
            Color c => c,
            SolidColorBrush sb => sb.Color,
            _ => fallback,
        };
    }

    private static object? Find(string key)
    {
        // Application 이 없는 환경(단위 테스트/디자이너)에서도 죽지 않게 한다.
        var app = Application.Current;
        if (app is null) return null;
        try { return app.TryFindResource(key); }
        catch { return null; }
    }

    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
