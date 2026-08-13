using System.Text.RegularExpressions;

namespace UartTerminal.Tests;

/// <summary>
/// 프로젝트 규약을 <b>소스에서 직접</b> 검사한다.
///
/// 왜 이런 테스트가 필요한가: "색은 테마 사전에만 둔다"·"문자열은 Loc 표에만 둔다" 는 규약을
/// 주석과 README 로만 선언해 두었더니, XAML 과 Core 에서는 지켜지고 <b>코드비하인드의 직접 대입에서만</b>
/// 반복해서 새어 나갔다. 그 결과 라이트 테마에서 안 보이는 색과 영어 모드에서 안 바뀌는 문자열이 쌓였고,
/// 번역까지 끝낸 Loc 키가 연결되지 않은 채 죽어 있기도 했다.
/// 자동 검사가 없으면 선언은 문서일 뿐이므로, 여기서 기계적으로 막는다.
/// </summary>
public sealed class ConventionTests
{
    // ── 리포지토리 경로 찾기 ────────────────────────────────────────────────

    /// <summary>테스트 어셈블리 위치에서 위로 올라가 솔루션 파일이 있는 폴더를 찾는다.</summary>
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UartTerminal.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir); // 빌드 출력 위치가 바뀌면 여기서 알려준다
            return dir!.FullName;
        }
    }

    private static string SrcRoot => Path.Combine(RepoRoot, "src");
    private static string ThemesDir => Path.Combine(SrcRoot, "UartTerminal", "Themes");
    private static string LocFile => Path.Combine(SrcRoot, "UartTerminal", "Loc.cs");

    /// <summary>src 아래의 소스 파일 전부(빌드 산출물 제외).</summary>
    private static List<string> SourceFiles(params string[] extensions) =>
        Directory.EnumerateFiles(SrcRoot, "*.*", SearchOption.AllDirectories)
            .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

    private static string Rel(string path) => Path.GetRelativePath(RepoRoot, path);

    /// <summary>따옴표 밖에 있는 <c>//</c> 부터 잘라낸다(문자열 안의 <c>//</c> 는 건드리지 않는다).</summary>
    private static string StripLineComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '\\' && inString) { i++; continue; }          // 이스케이프 한 글자 건너뜀
            if (c == '"') { inString = !inString; continue; }
            if (!inString && c == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    // ── 팔레트 ──────────────────────────────────────────────────────────────

    private static HashSet<string> PaletteKeys(string file) =>
        Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)""")
             .Select(m => m.Groups[1].Value)
             .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 팔레트는 <b>다크 하나뿐</b>이어야 한다(v1.8.0에서 테마 전환 제거).
    /// 라이트 팔레트가 되살아나면 키 동기화·대비 검사·전환 배선이 함께 따라오므로,
    /// 되돌리는 것 자체를 막지는 않되 <b>의식적인 결정</b>이 되도록 여기서 실패시킨다.
    /// </summary>
    [Fact]
    public void OnlyDarkPalette_Exists()
    {
        var palettes = Directory.GetFiles(ThemesDir, "Palette.*.xaml").Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "Palette.Dark.xaml" }, palettes);
    }

    /// <summary>ANSI 16색은 팔레트에 있어야 한다 — 코드에 두었더니 색을 고쳐도 화면이 안 따라왔다.</summary>
    [Fact]
    public void Palette_DefinesAllAnsiColors()
    {
        var keys = PaletteKeys(Path.Combine(ThemesDir, "Palette.Dark.xaml"));
        for (int i = 0; i < 16; i++)
            Assert.True(keys.Contains($"C.Ansi{i}"), $"C.Ansi{i} 누락");
    }

    /// <summary>
    /// 팔레트 <b>브러시</b> 참조는 XAML 에서 <c>{DynamicResource}</c> 로 통일한다.
    ///
    /// 테마 전환이 없어진 뒤로는 StaticResource 여도 동작은 같다. 그럼에도 규칙을 유지하는 이유는
    /// 두 가지다: (1) 두 방식이 섞이면 "여긴 왜 다르지" 가 반복되고, 어느 쪽이 맞는지 판단하려면
    /// 매번 테마 이력을 알아야 한다. (2) 대비 조정처럼 팔레트 값을 실행 중에 바꿔 보는 작업에서
    /// DynamicResource 쪽이 즉시 반영된다.
    /// 폰트 크기·지오메트리·스타일 키는 값이 바뀔 일이 없으므로 StaticResource 가 맞다.
    /// </summary>
    [Fact]
    public void PaletteBrushReferences_UseDynamicResource()
    {
        var brushKeys = Regex.Matches(
                File.ReadAllText(Path.Combine(ThemesDir, "Palette.Dark.xaml")),
                @"<SolidColorBrush x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (string file in SourceFiles(".xaml"))
        {
            if (Path.GetFileName(file).StartsWith("Palette.", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{StaticResource\s+([A-Za-z0-9_.]+)\}"))
            {
                if (!brushKeys.Contains(m.Groups[1].Value)) continue;
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Rel(file)}:{line} {m.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "팔레트 브러시는 DynamicResource 로 참조하세요:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>렌더러가 팔레트와 <b>같은 키 이름</b>으로 읽는지 확인(오타면 조용히 fallback 된다).</summary>
    [Fact]
    public void TerminalPalette_ReadsAnsiKeysFromTheme()
    {
        string src = File.ReadAllText(Path.Combine(SrcRoot, "UartTerminal", "Rendering", "TerminalPalette.cs"));
        Assert.Contains("\"C.Ansi{i}\"", src);
        Assert.Contains("\"C.TermSelection\"", src);   // 선택 배경도 테마에서(예전엔 코드 리터럴)
    }

    // ── 대비(가독성) ────────────────────────────────────────────────────────

    private static double Relative(byte v)
    {
        double c = v / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance((byte R, byte G, byte B) c) =>
        0.2126 * Relative(c.R) + 0.7152 * Relative(c.G) + 0.0722 * Relative(c.B);

    private static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>팔레트 XAML 에서 <c>#AARRGGBB</c>/<c>#RRGGBB</c> 값을 읽는다(알파는 대비 계산에서 무시).</summary>
    private static (byte R, byte G, byte B) PaletteColor(string file, string key)
    {
        var m = Regex.Match(File.ReadAllText(file), $@"x:Key=""{Regex.Escape(key)}"">\s*(#[0-9A-Fa-f]{{6,8}})\s*<");
        Assert.True(m.Success, $"{Path.GetFileName(file)}: {key} 값을 읽을 수 없습니다");
        string hex = m.Groups[1].Value.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];   // AA 제거
        return (Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    /// <summary>
    /// 강조 배경(선택 행·기본 버튼) 위의 글자색은 본문 최소 대비(4.5:1)를 넘겨야 한다.
    /// 예전에는 이 색이 <c>"White"</c> 리터럴로 7곳에 흩어져 있어서 팔레트 검사 밖에 있었고,
    /// AccentDeep 을 밝게 조정하면 조용히 무너질 수 있었다 — 토큰(C.OnAccent)으로 끌어와 잠근다.
    /// </summary>
    [Fact]
    public void OnAccentText_IsReadableOnAccentBackground()
    {
        string file = Path.Combine(ThemesDir, "Palette.Dark.xaml");
        double r = Contrast(PaletteColor(file, "C.OnAccent"), PaletteColor(file, "C.AccentDeep"));
        Assert.True(r >= 4.5, $"선택 행 글자 대비 {r:F2}:1 — 4.5:1 이상이어야 합니다");
    }

    /// <summary>
    /// 다크 테마는 xterm 표준값을 대체로 유지하지만, <b>red 만은</b> 예외로 올렸다 —
    /// #CD0000 은 3.24:1 로 16색 중 최저였고 그게 하필 <c>ESP_LOGE</c>(가장 중요한 줄)였다.
    /// (검정 0번이 다크 배경에서 1.1:1 인 것은 ANSI 규약상 불가피하므로 검사하지 않는다.)
    /// </summary>
    [Fact]
    public void DarkPalette_ErrorRed_IsReadable()
    {
        string file = Path.Combine(ThemesDir, "Palette.Dark.xaml");
        var bg = PaletteColor(file, "C.TermBg");

        double red = Contrast(PaletteColor(file, "C.Ansi1"), bg);
        double brightRed = Contrast(PaletteColor(file, "C.Ansi9"), bg);

        Assert.True(red >= 4.5, $"ANSI red(ESP_LOGE) 대비 {red:F2}:1 — 4.5:1 이상이어야 합니다");
        // bold 승격(red→bright red)이 더 흐려지면 강조가 뒤집힌다.
        Assert.True(brightRed >= red, $"bright red {brightRed:F2}:1 < red {red:F2}:1");
    }

    /// <summary>
    /// 색 리터럴(<c>#RRGGBB</c>)은 <c>Themes/</c> 안에만 있어야 한다.
    /// 앱 XAML 에 직접 박은 색은 테마 전환을 따라오지 않는다(찾기 바·줌 표시기가 실제로 그랬다).
    /// </summary>
    [Fact]
    public void AppXaml_HasNoHardcodedColorLiterals()
    {
        var offenders = new List<string>();
        foreach (string file in SourceFiles(".xaml"))
        {
            if (Path.GetDirectoryName(file)!.EndsWith("Themes", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text,
                @"(Background|Foreground|BorderBrush|Fill|Stroke|Color)=""(#[0-9A-Fa-f]{6,8})"""))
            {
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Rel(file)}:{line} {m.Groups[1].Value}=\"{m.Groups[2].Value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "색을 Themes/ 팔레트로 옮기세요:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 코드비하인드에서 <c>new SolidColorBrush(Color.FromRgb(...))</c> 로 색을 만들면 안 된다 —
    /// <c>Theme.Brush(key)</c> 로 읽어야 테마를 따라온다. MCP 상태 문구가 이 방식으로 굳어
    /// 라이트 테마에서 1.8:1 이 되어 '읽기 전용' 표시가 사라졌다.
    /// </summary>
    [Fact]
    public void CodeBehind_DoesNotBuildBrushesFromLiteralColors()
    {
        var offenders = new List<string>();
        foreach (string file in SourceFiles(".cs"))
        {
            // 팔레트 대체값(Theme.ColorOr 의 fallback)과 렌더러 기본값 정의는 예외 — 테마가 없는
            // 환경(단위 테스트/디자이너)을 위한 것이고, 실제 색은 테마에서 읽는다.
            string name = Path.GetFileName(file);
            if (name is "TerminalPalette.cs" or "Theme.cs") continue;

            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"new SolidColorBrush\(\s*Color\.From"))
            {
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Rel(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Theme.Brush(key) 로 바꾸세요:\n  " + string.Join("\n  ", offenders));
    }

    // ── 디자인 토큰(타이포·색) ───────────────────────────────────────────────

    /// <summary>테마 사전을 뺀 '화면' XAML(다이얼로그·셸·문서 뷰).</summary>
    private static List<string> ScreenXaml() =>
        SourceFiles(".xaml")
            .Where(p => !Path.GetDirectoryName(p)!.EndsWith("Themes", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// 화면 XAML 에 <c>FontSize</c> 숫자를 직접 쓰지 않는다 — <c>{StaticResource Font.*}</c> 만.
    ///
    /// 이 규칙이 없던 동안 화면마다 10 / 10.5 / 11 / 11.5 / 12 / 12.5 / 13 이 즉석에서 정해져
    /// 57곳에 박혔다. 0.5px 차이는 한 화면만 보면 아무도 못 잡고, 새 화면마다 값이 하나씩
    /// 더 늘어난다 — "프로그램이 점점 이상해진다" 의 실체가 이것이다.
    /// </summary>
    [Fact]
    public void ScreenXaml_HasNoFontSizeLiterals()
    {
        var offenders = new List<string>();
        foreach (string file in ScreenXaml())
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"FontSize=""[0-9]"))
            {
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Rel(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "FontSize 는 Controls.xaml 의 Font.* 토큰을 쓰세요:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 이름 있는 색(<c>White</c>, <c>Black</c> …)도 색 리터럴이다. <c>#RRGGBB</c> 검사는 이미
    /// 있었지만 이름 색은 그물을 빠져나가, 선택 행 글자색 <c>"White"</c> 가 7곳에 복붙돼 있었다.
    /// (<c>Transparent</c> 는 색이 아니라 '칠하지 않음'/히트테스트 용도라 예외.)
    /// </summary>
    [Fact]
    public void Xaml_HasNoNamedColorLiterals()
    {
        string[] named = { "White", "Black", "Gray", "Red", "Green", "Blue", "Yellow", "Orange", "Magenta" };
        var offenders = new List<string>();

        foreach (string file in SourceFiles(".xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text,
                @"(?:Background|Foreground|BorderBrush|Fill|Stroke|Color)=""([A-Za-z]+)""|<Setter\s+Property=""(?:Background|Foreground|BorderBrush|Fill|Stroke)""\s+Value=""([A-Za-z]+)"""))
            {
                string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!named.Contains(v, StringComparer.Ordinal)) continue;
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Rel(file)}:{line} {v}");
            }
        }

        Assert.True(offenders.Count == 0,
            "이름 있는 색도 팔레트 키로 옮기세요:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// XAML 이 참조하는 리소스 키는 전부 어딘가에 정의돼 있어야 한다.
    ///
    /// <b>왜 이 검사가 필요한가</b>: 리소스 키 오타는 컴파일이 아니라 <b>창을 여는 순간</b>
    /// 예외로 터진다. 빌드와 단위 테스트를 다 통과하고도 사용자가 그 다이얼로그를 열었을 때
    /// 처음 드러나는 종류의 결함이라, 정적으로 잡아 두지 않으면 잡을 방법이 사실상 없다.
    /// (토큰·스타일 이름을 바꾸는 리팩터링에서 특히 쉽게 난다.)
    ///
    /// 한계: 정의 측을 모든 XAML 에서 모으므로 <b>다른 창의 Window.Resources 키</b>를 참조해도
    /// 통과한다(그건 런타임에 실패한다). 오타·이름 변경 누락을 잡는 것이 이 검사의 목적이다.
    /// </summary>
    [Fact]
    public void EveryXamlResourceReference_IsDefined()
    {
        // 정의 측: 모든 XAML 의 x:Key (테마 사전 + 각 화면의 Window.Resources)
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles(".xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
                defined.Add(m.Groups[1].Value);

        var missing = new List<string>();
        int seen = 0;
        foreach (string file in SourceFiles(".xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\}"))
            {
                seen++;
                if (defined.Contains(m.Groups[1].Value)) continue;
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                missing.Add($"{Rel(file)}:{line} {m.Groups[1].Value}");
            }
        }

        // 정규식이 아무것도 못 잡으면 이 테스트는 조용히 무의미해진다(빈 통과 방지).
        Assert.True(seen > 200, $"리소스 참조를 {seen}건밖에 못 찾았습니다 — 정규식을 확인하세요");
        Assert.True(missing.Count == 0,
            "정의되지 않은 리소스 키(창을 열 때 예외로 터진다):\n  " + string.Join("\n  ", missing));
    }

    // ── Loc 키 ──────────────────────────────────────────────────────────────

    private static HashSet<string> DefinedLocKeys() =>
        Regex.Matches(File.ReadAllText(LocFile), @"\[""([^""]+)""\]\s*=\s*\(")
             .Select(m => m.Groups[1].Value)
             .ToHashSet(StringComparer.Ordinal);

    /// <summary>Loc.cs 를 제외한 모든 소스에서 참조된 키(코드의 "키" 리터럴 + XAML 의 {loc:Str 키}).</summary>
    private static HashSet<string> ReferencedLocKeys()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles(".cs", ".xaml"))
        {
            if (string.Equals(file, LocFile, StringComparison.OrdinalIgnoreCase)) continue;
            string text = File.ReadAllText(file);

            // XAML: {loc:Str Menu.Terminal} / {loc:Str Key=Menu.Terminal}
            foreach (Match m in Regex.Matches(text, @"\{loc:Str\s+(?:Key=)?([A-Za-z0-9_.]+)\s*\}"))
                referenced.Add(m.Groups[1].Value);

            // 코드: Loc.S("K") / Loc.F("K", …) / Loc.Bind("K") / LocMessage.Of("K", …)
            // 그리고 변수로 넘기는 키(예: RunControlSequenceAsync 의 whatKey)까지 잡기 위해
            // '점이 있는 따옴표 문자열' 을 모두 후보로 본다 — 키가 아닌 것은 아래 교집합에서 걸러진다.
            foreach (Match m in Regex.Matches(text, @"""([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)"""))
                referenced.Add(m.Groups[1].Value);
        }
        return referenced;
    }

    /// <summary>없는 키를 참조하면 화면에 <c>[키]</c> 가 그대로 찍힌다.</summary>
    [Fact]
    public void EveryReferencedLocKey_IsDefined()
    {
        var defined = DefinedLocKeys();
        var used = ReferencedLocKeys();

        // 참조 후보에는 파일명·타입명도 섞이므로, Loc 표에 있는 접두사를 쓰는 것만 검사한다.
        var prefixes = defined.Select(k => k.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var missing = used.Where(k => prefixes.Contains(k.Split('.')[0]))
                          .Where(k => !defined.Contains(k))
                          .OrderBy(k => k)
                          .ToList();

        Assert.True(missing.Count == 0, $"정의되지 않은 Loc 키 참조: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// 정의됐지만 아무도 안 쓰는 키는 <b>연결을 잊은 흔적</b>이다.
    /// 실제로 <c>Doc.LogSaveTitle</c>("Save Log")·<c>Doc.LogFilter</c> 가 번역까지 끝난 채 죽어 있었고,
    /// 정작 같은 자리 코드에는 한국어가 하드코딩돼 있었다.
    /// </summary>
    [Fact]
    public void EveryDefinedLocKey_IsReferenced()
    {
        var defined = DefinedLocKeys();
        var used = ReferencedLocKeys();
        var dead = defined.Where(k => !used.Contains(k)).OrderBy(k => k).ToList();

        Assert.True(dead.Count == 0,
            $"쓰이지 않는 Loc 키 {dead.Count}개 — 연결하거나 지우세요: {string.Join(", ", dead)}");
    }

    /// <summary>
    /// 한국어 리터럴이 있어도 되는 파일. <b>사용자 화면에 도달하지 않는 것만</b> 넣는다.
    /// (이 목록을 늘려 테스트를 통과시키지 말 것 — 화면 문자열은 Loc 표로 옮긴다.)
    /// </summary>
    private static readonly string[] KoreanAllowedFiles =
    {
        "Loc.cs",           // 번역 표 자체
        "UartMcpTools.cs",  // MCP 툴 설명 — AI 에게 가는 문서이지 UI 문자열이 아니다
        "McpPipeServer.cs", // 같음(서버 instructions)
    };

    /// <summary>
    /// 한국어 리터럴이 허용되는 프로젝트. <c>McpRelay</c> 는 Loc 을 참조하지 않는 별도 콘솔 exe 이고
    /// 출력은 개발자용 stderr 다.
    /// </summary>
    private const string KoreanAllowedProject = "UartTerminal.McpRelay";

    /// <summary>
    /// 사용자에게 보이는 한국어가 코드에 하드코딩되면 영어 모드에서 그대로 남는다.
    ///
    /// 문장을 만드는 위치를 분류하는 대신 <b>"허용 목록 밖에는 한국어 리터럴이 없다"</b> 로 검사한다 —
    /// 처음엔 대입문 패턴(<c>.Text =</c> 등)으로 찾았는데 객체 초기화자·여러 줄 호출·return 식을
    /// 놓쳤다(5건만 잡고 실제로는 62건이었다).
    ///
    /// 예외가 필요한 줄에는 <c>// loc:data</c> 를 붙인다 — 폰트 패밀리 이름이나 파일에 저장되는
    /// 그룹 이름처럼 <b>번역하면 오히려 깨지는 데이터</b>가 그 대상이다.
    /// 주석과 진단 로그(DiagLog)는 프로젝트 규약상 한국어이므로 검사에서 빠진다.
    /// </summary>
    [Fact]
    public void UserFacingText_HasNoHardcodedKorean()
    {
        var offenders = new List<string>();
        var korean = new Regex(@"""(?:[^""\\]|\\.)*[가-힣](?:[^""\\]|\\.)*""", RegexOptions.Compiled);

        foreach (string file in SourceFiles(".cs"))
        {
            if (KoreanAllowedFiles.Contains(Path.GetFileName(file), StringComparer.Ordinal)) continue;
            if (Rel(file).Contains(KoreanAllowedProject, StringComparison.Ordinal)) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith('/') || trimmed.StartsWith('*')) continue;  // 주석
                if (line.Contains("DiagLog.")) continue;                            // 진단 로그
                if (line.Contains("loc:data")) continue;                            // 명시적 예외
                // 줄 끝 주석을 떼고 본다 — 안 떼면 두 문자열 리터럴 <b>사이</b>의 주석 텍스트가
                // 하나의 문자열로 잡혀 오검출된다(실제로 그랬다).
                if (!korean.IsMatch(StripLineComment(line))) continue;
                offenders.Add($"{Rel(file)}:{i + 1} {trimmed[..Math.Min(trimmed.Length, 100)]}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"화면 문자열 {offenders.Count}건을 Loc 키로 옮기세요(데이터면 // loc:data):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// <c>Loc.Changed</c> 에 구독자가 있어야 한다. 이 이벤트는 <b>코드가 조립해 보관한</b> 문자열
    /// (탭 제목·상태바·칩 툴팁)을 다시 만들기 위한 것인데, 발생만 하고 받는 곳이 하나도 없어서
    /// 언어를 바꿔도 그 문자열들이 옛 언어로 남아 있었다. 주석은 이 배선을 전제로 쓰여 있었다.
    /// </summary>
    [Fact]
    public void LocChanged_HasAtLeastOneSubscriber()
    {
        var subscribers = SourceFiles(".cs")
            .Where(f => !string.Equals(f, LocFile, StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("Loc.Changed +="))
            .Select(Rel)
            .ToList();

        Assert.True(subscribers.Count > 0,
            "Loc.Changed 를 구독하는 곳이 없습니다 — 코드가 만든 문자열이 언어 전환을 따라오지 않습니다.");
    }

    /// <summary>정적 이벤트 구독은 짝이 맞아야 한다(해지가 없으면 창/뷰가 영구히 살아남는다).</summary>
    [Theory]
    [InlineData("Loc.Changed")]
    public void StaticEventSubscriptions_ArePaired(string evt)
    {
        var unpaired = new List<string>();
        foreach (string file in SourceFiles(".cs"))
        {
            string text = File.ReadAllText(file);
            int subs = Regex.Matches(text, Regex.Escape(evt) + @"\s*\+=").Count;
            int unsubs = Regex.Matches(text, Regex.Escape(evt) + @"\s*-=").Count;
            if (subs > 0 && unsubs == 0)
                unpaired.Add($"{Rel(file)} ({subs}회 구독, 해지 0회)");
        }

        Assert.True(unpaired.Count == 0,
            $"{evt} 구독 해지가 없습니다:\n  " + string.Join("\n  ", unpaired));
    }
}
