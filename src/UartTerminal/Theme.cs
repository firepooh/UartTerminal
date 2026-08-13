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
    /// 테마를 바꾼다: 팔레트 사전의 <b>값을 대상 테마 값으로 교체</b>한다.
    ///
    /// 이 값 교체가 화면에 반영되는 경로는 둘이다.
    ///  · XAML 은 팔레트 브러시를 전부 <c>{DynamicResource}</c> 로 참조한다 — 사전 값이 바뀌면
    ///    WPF 가 참조 지점을 다시 평가한다.
    ///  · 코드는 <see cref="Brush"/>·<see cref="Color"/> 로 쓰는 순간 조회한다(터미널 렌더러 포함).
    ///
    /// <b>왜 이 방식인가(시행착오 기록)</b>: 처음엔 '브러시 인스턴스를 유지하고 Color 만 제자리 변경 +
    /// StaticResource' 로 설계했는데, 측정해 보니 그 경로는 <b>한 번도 실행된 적이 없다</b> —
    /// WPF 는 BAML 로 읽은 사전의 Freezable 을 자동 Freeze 하고, 사전에 새로 넣는 값도 즉시 다시
    /// Freeze 한다(38/38 확인). 그래서 항상 값 교체로 흘렀고, StaticResource 는 로드 시점 값이
    /// 박혀서 실행 중 전환이 메뉴·상태바 등엔 먹지 않았다. DynamicResource 전환으로 해결.
    /// (사전을 통째로 교체하는 방식은 이미 렌더된 컨트롤에 반영되지 않는 것을 확인했다 — 값 교체여야 한다.)
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
        // (ConventionTests 가 두 팔레트의 키 집합 동일성을 빌드 시점에 검사한다.)
        foreach (object key in live.Keys)
            if (!target.Contains(key))
                DiagLog.Warn($"{theme} 팔레트에 키 없음: {key}");

        foreach (object key in target.Keys)
            live[key] = target[key]!;

        Current = theme;
        TerminalPalette.Reload();     // 렌더러가 스냅샷한 기본색·ANSI 16색 갱신
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
