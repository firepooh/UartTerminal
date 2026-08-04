using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace UartTerminal.Core.Flash;

/// <summary>
/// 패키지 zip 을 작업 폴더에 푸는 유틸.
///  - <b>Zip Slip 방어</b>: 엔트리 경로가 대상 폴더를 벗어나면 거부한다(zip 은 외부에서 온 파일이다).
///  - <b>평면화</b>: 배포 zip 은 대개 평면이고 해석도 파일명 기준이라, 폴더가 있어도 파일명만 남겨 푼다.
///    (같은 파일명이 두 폴더에 있으면 그때만 폴더를 유지한다)
///  - <b>재사용</b>: 같은 zip 을 다시 고르면 이미 푼 폴더를 그대로 쓴다(이름+해시로 폴더 결정).
/// </summary>
public static class FlashExtractor
{
    /// <summary>zip 경로로부터 재사용 가능한 작업 폴더명을 만든다(같은 파일 → 같은 폴더).</summary>
    public static string WorkFolderName(string zipPath)
    {
        string stem = Path.GetFileNameWithoutExtension(zipPath);
        var fi = new FileInfo(zipPath);
        // 경로 + 크기 + 수정시각 → 짧은 해시. 내용을 읽지 않아 즉시 계산된다.
        string key = $"{zipPath.ToLowerInvariant()}|{(fi.Exists ? fi.Length : 0)}|{(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string tag = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        return $"{Sanitize(stem)}-{tag}";
    }

    /// <summary>
    /// zip 을 <paramref name="destDir"/> 에 푼다. 이미 모든 파일이 같은 크기로 있으면 건너뛴다.
    /// 반환값은 푼 파일들의 (파일명 → 전체 경로) 맵.
    /// </summary>
    public static Dictionary<string, string> Extract(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        string destFull = Path.GetFullPath(destDir);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);

        // 파일명 충돌이 있으면(드묾) 폴더 구조를 유지해 덮어쓰기를 피한다.
        var names = zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.Name).ToList();
        bool flatten = names.Count == names.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue; // 디렉터리

            // 악의적 엔트리는 '평면화 덕분에 우연히 무해해지는 것'에 기대지 않고 먼저 거부한다.
            EnsureSafeEntryPath(entry.FullName);

            string relative = flatten ? entry.Name : entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(destFull, relative));

            // Zip Slip: 대상 폴더 밖으로 나가는 경로는 거부.
            if (!target.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, destFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"zip 항목이 대상 폴더를 벗어납니다: {entry.FullName}");

            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var existing = new FileInfo(target);
            if (!existing.Exists || existing.Length != entry.Length)
                entry.ExtractToFile(target, overwrite: true);

            result[entry.Name] = target;
        }

        return result;
    }

    /// <summary>
    /// 엔트리 경로가 상위 폴더 탈출(<c>..</c>)이나 절대 경로를 시도하는지 검사한다.
    /// 파일명만 남기는 평면화가 결과적으로 탈출을 막더라도, <b>의도가 악의적인 zip 은 아예 거부</b>한다
    /// (파일명이 중복돼 폴더 구조를 유지하는 경로에서는 평면화가 방어해 주지 않는다).
    /// </summary>
    private static void EnsureSafeEntryPath(string fullName)
    {
        string norm = fullName.Replace('\\', '/');
        bool rooted = norm.StartsWith('/') || (norm.Length > 1 && norm[1] == ':');
        bool escapes = norm.Split('/').Any(seg => seg == "..");
        if (rooted || escapes)
            throw new IOException($"zip 항목이 대상 폴더를 벗어납니다: {fullName}");
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "package" : sb.ToString();
    }
}
