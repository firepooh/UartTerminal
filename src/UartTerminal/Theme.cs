using System.Windows;
using System.Windows.Media;
using UartTerminal.Rendering;

namespace UartTerminal;

/// <summary>선택 가능한 테마.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

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

    // ── 테마 전환 ────────────────────────────────────────────────────────────

    /// <summary>현재 적용된 테마.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>테마가 바뀐 뒤 발생(렌더러처럼 색을 스냅샷해 두는 곳이 다시 읽도록).</summary>
    public static event Action? Changed;

    /// <summary>
    /// 테마를 바꾼다. <b>사전을 교체하지 않고 팔레트 값만 덮어쓴다</b> —
    /// 사전 교체나 DynamicResource 는 이미 렌더된 컨트롤에 반영되지 않는 것을 실험으로 확인했다.
    /// 브러시는 같은 인스턴스의 <c>Color</c> 만 바꾸므로, StaticResource 로 브러시를 가져간
    /// 컨트롤(앱 XAML 200여 곳)도 손대지 않고 즉시 따라온다.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var live = LivePalette(app);
        if (live is null)
        {
            DiagLog.Warn("팔레트 사전을 찾지 못해 테마를 바꾸지 못했습니다.");
            return;
        }

        var target = LoadPalette(theme);
        if (target is null) return;

        // 키 집합이 어긋나면 한쪽 테마에서만 색이 남아 화면이 깨진다 → 진단에 남긴다.
        foreach (object key in live.Keys)
            if (!target.Contains(key))
                DiagLog.Warn($"{theme} 팔레트에 키 없음: {key}");

        foreach (object key in target.Keys)
        {
            object? next = target[key];
            object? now = live[key];

            // 브러시는 '인스턴스 유지 + 색만 교체'. 이게 이 방식의 핵심이다.
            if (now is SolidColorBrush cur && next is SolidColorBrush nb)
            {
                if (!cur.IsFrozen) { cur.Color = nb.Color; continue; }
                DiagLog.Warn($"브러시가 frozen 이라 색을 바꿀 수 없습니다: {key}");
            }

            live[key] = next!;   // Color 항목 등은 값 자체를 교체(코드에서 Theme.Color 로 읽는다)
        }

        Current = theme;
        TerminalPalette.Reload();     // 렌더러가 스냅샷한 기본색 갱신
        try { Changed?.Invoke(); } catch (Exception ex) { DiagLog.Exception("Theme.Changed", ex); }
        DiagLog.Info($"테마 적용: {theme}");
    }

    /// <summary>앱에 병합된 팔레트 사전(색·브러시가 들어 있는 쪽)을 찾는다.</summary>
    private static ResourceDictionary? LivePalette(Application app)
    {
        foreach (var d in app.Resources.MergedDictionaries)
            if (d.Contains("C.Bg")) return d;   // 팔레트만 갖는 표식 키
        return null;
    }

    private static ResourceDictionary? LoadPalette(AppTheme theme)
    {
        string file = theme == AppTheme.Light ? "Palette.Light.xaml" : "Palette.Dark.xaml";
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri($"/UartTerminal;component/Themes/{file}", UriKind.Relative),
            };
        }
        catch (Exception ex)
        {
            DiagLog.Exception($"팔레트 로드 실패: {file}", ex);
            return null;
        }
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
