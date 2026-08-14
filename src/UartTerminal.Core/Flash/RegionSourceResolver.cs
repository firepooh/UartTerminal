using System.Net.Http;

namespace UartTerminal.Core.Flash;

/// <summary>
/// 영역 굽기의 입력(BIN 파일 / zip / http 링크)을 <b>로컬 bin 파일 목록</b>으로 푼다.
///
///  - bin 파일: 그대로 쓴다(압축을 풀 필요도, 복사할 필요도 없다).
///  - zip 파일: 작업 폴더에 풀고(<see cref="FlashExtractor"/> — Zip Slip 방어 포함) *.bin 만 고른다.
///  - http(s) 링크: 내려받은 뒤 파일 종류에 따라 위 두 경로로 합류한다.
///
/// bin 이 여러 개 나오면 고르는 것은 UI 의 몫이다 — 여기서 "그럴듯한 것" 을 추측해 고르지 않는다
/// (잘못된 파일을 잘못된 주소에 구우면 장비가 손상된다).
/// </summary>
public static class RegionSourceResolver
{
    // 다운로드 전용 클라이언트. 재지정(redirect)은 기본 허용, 타임아웃은 대용량 이미지 기준.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// URL 을 <paramref name="destDir"/> 에 내려받고 저장된 경로를 돌려준다.
    /// 파일명은 URL 마지막 조각에서 얻되, 경로로 쓸 수 없는 이름이면 고정 이름으로 대체한다.
    /// </summary>
    public static async Task<string> DownloadAsync(string url, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        string name = "download.bin";
        try
        {
            string last = new Uri(url).Segments.LastOrDefault()?.Trim('/') ?? "";
            last = Uri.UnescapeDataString(last);
            if (last.Length > 0 && last.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                name = last;
        }
        catch { /* 이상한 URL 이면 고정 이름으로 */ }

        string target = Path.Combine(destDir, name);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // 임시 파일에 받은 뒤 교체 — 끊긴 다운로드가 완성본처럼 남지 않게.
        string tmp = target + ".part";
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            await src.CopyToAsync(dst, ct);
        File.Move(tmp, target, overwrite: true);

        return target;
    }

    /// <summary>
    /// 로컬 파일(bin 또는 zip)을 bin 목록으로 푼다. zip 이면 <paramref name="workRoot"/> 아래에 푼다.
    /// zip 인지 여부는 확장자가 아니라 <b>매직 넘버(PK)</b>로 판정한다 — 다운로드 파일은 이름을 믿을 수 없다.
    /// </summary>
    public static IReadOnlyList<string> ResolveBins(string filePath, string workRoot)
    {
        if (!IsZip(filePath))
            return new[] { filePath };

        string dest = Path.Combine(workRoot, FlashExtractor.WorkFolderName(filePath));
        var map = FlashExtractor.Extract(filePath, dest);
        return map.Values
            .Where(p => string.Equals(Path.GetExtension(p), ".bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[2];
            return fs.Read(sig) == 2 && sig[0] == (byte)'P' && sig[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }
}
