using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartTerminal.Core.Flash;

/// <summary>찾아낸 esptool 실행 파일과 그 버전(CLI 문법이 v4/v5 에서 다르다).</summary>
public sealed record EsptoolInfo(string Path, string VersionText, int Major)
{
    /// <summary>
    /// v5 부터 명령·옵션이 하이픈으로 바뀌었다(<c>write_flash</c>→<c>write-flash</c>,
    /// <c>--flash_mode</c>→<c>--flash-mode</c>). 이걸 틀리면 즉시 실패한다.
    /// </summary>
    public bool UsesHyphenSyntax => Major >= 5;
}

/// <summary>
/// esptool 탐색에 필요한 환경(테스트에서 파일 시스템을 대신 넣을 수 있게 이음새로 분리).
/// </summary>
public sealed record EsptoolSearch
{
    /// <summary>사용자가 설정에서 직접 지정한 경로(최우선).</summary>
    public string? UserPath { get; init; }

    /// <summary>앱 폴더. <c>tools\esptool\esptool.exe</c> 를 번들하면 개발환경 없는 PC 에서도 동작한다.</summary>
    public string? AppDirectory { get; init; }

    /// <summary><c>%USERPROFILE%\.espressif</c> — ESP-IDF 설치 PC.</summary>
    public string? EspressifRoot { get; init; }

    public IReadOnlyList<string> PathDirectories { get; init; } = Array.Empty<string>();

    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, string> ReadAllText { get; init; } = File.ReadAllText;

    /// <summary>디렉터리 목록(없으면 빈 목록). 예: python_env 하위 환경들.</summary>
    public Func<string, IEnumerable<string>> EnumerateDirectories { get; init; } =
        d => Directory.Exists(d) ? Directory.EnumerateDirectories(d) : Enumerable.Empty<string>();

    public static EsptoolSearch Default(string? userPath = null, string? appDirectory = null) => new()
    {
        UserPath = userPath,
        AppDirectory = appDirectory ?? AppContext.BaseDirectory,
        EspressifRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espressif"),
        PathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
    };
}

/// <summary>
/// esptool 실행 파일을 찾는다. 개발 PC(ESP-IDF 설치)와 그렇지 않은 PC 를 모두 커버하도록
/// <b>번들 → IDF 설치 → PATH</b> 순으로 훑고, 사용자 지정 경로는 언제나 최우선이다.
/// </summary>
public static class EsptoolLocator
{
    public const string ExeName = "esptool.exe";

    /// <summary>앱과 함께 배포할 때 두는 상대 경로.</summary>
    public const string BundledRelativePath = @"tools\esptool\esptool.exe";

    private static readonly Regex VersionPattern = new(@"v?(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>탐색 순서대로 후보 경로를 만든다(존재하는 것만).</summary>
    public static IReadOnlyList<string> Candidates(EsptoolSearch search)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!search.FileExists(path!)) return;
            if (seen.Add(path!)) found.Add(path!);
        }

        // 1) 사용자 지정
        TryAdd(search.UserPath);

        // 2) 앱과 함께 배포한 번들
        if (search.AppDirectory is { Length: > 0 } appDir)
            TryAdd(Path.Combine(appDir, BundledRelativePath));

        // 3) ESP-IDF 설치본 — 선택된 IDF 의 python 환경을 먼저 본다
        if (search.EspressifRoot is { Length: > 0 } root)
        {
            foreach (string p in FromEspressif(root, search)) TryAdd(p);
        }

        // 4) PATH
        foreach (string dir in search.PathDirectories)
            TryAdd(Path.Combine(dir, ExeName));

        return found;
    }

    /// <summary>
    /// <c>.espressif\esp_idf.json</c> 은 IDE 용 메타데이터로, 설치된 IDF 별 python 경로와
    /// 현재 선택(<c>idfSelectedId</c>)을 담고 있다. 선택된 환경의 esptool 을 우선 쓰면
    /// 사용자가 쓰는 IDF 와 버전이 어긋나지 않는다.
    /// </summary>
    private static IEnumerable<string> FromEspressif(string root, EsptoolSearch search)
    {
        var ordered = new List<string>();

        string jsonPath = Path.Combine(root, "esp_idf.json");
        if (search.FileExists(jsonPath))
        {
            string? selectedPython = null;
            var others = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(search.ReadAllText(jsonPath));
                var r = doc.RootElement;
                string? selectedId = r.TryGetProperty("idfSelectedId", out var sid) ? sid.GetString() : null;

                if (r.TryGetProperty("idfInstalled", out var installed) &&
                    installed.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in installed.EnumerateObject())
                    {
                        if (!entry.Value.TryGetProperty("python", out var py)) continue;
                        string? python = py.GetString();
                        if (string.IsNullOrWhiteSpace(python)) continue;

                        if (string.Equals(entry.Name, selectedId, StringComparison.OrdinalIgnoreCase))
                            selectedPython = python;
                        else
                            others.Add(python!);
                    }
                }
            }
            catch
            {
                // 메타데이터가 깨져도 아래 디렉터리 훑기로 넘어간다.
            }

            if (selectedPython is not null) ordered.Add(SiblingEsptool(selectedPython));
            ordered.AddRange(others.Select(SiblingEsptool));
        }

        // 메타데이터에 없더라도 python_env 하위를 직접 훑는다(버전 내림차순 — 최신 우선).
        string envRoot = Path.Combine(root, "python_env");
        foreach (string dir in search.EnumerateDirectories(envRoot)
                     .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(Path.Combine(dir, "Scripts", ExeName));
        }

        return ordered;
    }

    /// <summary>python.exe 와 같은 Scripts 폴더에 esptool.exe 가 있다.</summary>
    private static string SiblingEsptool(string pythonPath)
    {
        string? dir = Path.GetDirectoryName(pythonPath);
        return string.IsNullOrEmpty(dir) ? ExeName : Path.Combine(dir, ExeName);
    }

    /// <summary>
    /// <c>esptool version</c> 출력에서 주 버전을 뽑는다.
    /// v4 는 "esptool.py v4.9.1", v5 는 "esptool v5.3.1" 처럼 찍는다.
    /// </summary>
    public static int ParseMajorVersion(string versionOutput)
    {
        foreach (var line in (versionOutput ?? "").Split('\n'))
        {
            var m = VersionPattern.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int major) && major is > 0 and < 100)
                return major;
        }
        return 0;
    }

    /// <summary>
    /// 후보들을 차례로 <c>version</c> 실행해 첫 성공을 채택한다.
    /// <paramref name="probe"/> 는 (exe 경로) → 표준출력 을 돌려주는 실행기(테스트에서 대체 가능).
    /// </summary>
    public static EsptoolInfo? Resolve(EsptoolSearch search, Func<string, string?> probe)
    {
        foreach (string path in Candidates(search))
        {
            string? output;
            try { output = probe(path); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(output)) continue;

            int major = ParseMajorVersion(output!);
            if (major == 0) continue;

            string text = output!.Split('\n').FirstOrDefault(l => l.Contains('v'))?.Trim() ?? output!.Trim();
            return new EsptoolInfo(path, text, major);
        }
        return null;
    }
}
