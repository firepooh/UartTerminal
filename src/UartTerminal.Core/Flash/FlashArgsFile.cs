using System.Globalization;
using System.Text.Json;

namespace UartTerminal.Core.Flash;

/// <summary>플래시 대상 한 항목(오프셋 + ESP-IDF 가 가리키는 파일 경로).</summary>
public readonly record struct FlashArgEntry(uint Offset, string Path)
{
    /// <summary>
    /// 파일명만. <c>flash_project_args</c> 는 <c>bootloader/bootloader.bin</c> 처럼 <b>빌드 트리 기준 경로</b>를
    /// 담는데 배포 zip 은 보통 평면(flat)이라, 실제 매칭은 이 파일명으로 해야 한다.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Path.Replace('\\', '/'));
}

/// <summary>
/// ESP-IDF 가 만들어 주는 플래시 인자 파일 파서. 두 형식을 지원한다:
///  - <c>flash_project_args</c> (텍스트: 첫 줄 옵션, 이후 "&lt;오프셋&gt; &lt;경로&gt;")
///  - <c>flasher_args.json</c> (JSON: <c>flash_files</c> 맵 + <c>extra_esptool_args.chip</c>)
/// 오프셋을 사람이 손으로 입력하지 않아도 되게 하는 근거 파일이다.
/// </summary>
public sealed record FlashArgs
{
    public IReadOnlyList<FlashArgEntry> Entries { get; init; } = Array.Empty<FlashArgEntry>();

    /// <summary>파일에 적힌 SPI 설정(없으면 null). 기본은 '바이너리 그대로(keep)' 라 참고용이다.</summary>
    public string? FlashMode { get; init; }
    public string? FlashFreq { get; init; }
    public string? FlashSize { get; init; }

    /// <summary>flasher_args.json 만 갖는 칩 정보(flash_project_args 에는 없다).</summary>
    public EspChip Chip { get; init; } = EspChip.Unknown;

    public static FlashArgs Empty => new();
}

public static class FlashArgsFile
{
    /// <summary>zip/폴더에서 찾을 후보 파일명(우선순위 순).</summary>
    public static readonly string[] CandidateNames = { "flasher_args.json", "flash_project_args", "flash_args" };

    /// <summary>내용으로 형식을 자동 판별해 파싱한다.</summary>
    public static FlashArgs Parse(string fileName, string content)
    {
        string trimmed = content.TrimStart();
        bool looksJson = trimmed.StartsWith('{')
                         || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        return looksJson ? ParseJson(content) : ParseText(content);
    }

    /// <summary>
    /// <c>flash_project_args</c>:
    /// <code>
    /// --flash_mode dio --flash_freq 80m --flash_size 16MB
    /// 0x0 bootloader/bootloader.bin
    /// 0x10000 VMS.bin
    /// </code>
    /// 옵션 줄은 어디에 있어도 되고(<c>--</c> 로 시작), 오프셋 줄은 순서를 그대로 보존한다.
    /// </summary>
    public static FlashArgs ParseText(string content)
    {
        var entries = new List<FlashArgEntry>();
        string? mode = null, freq = null, size = null;

        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i + 1 < tok.Length; i++)
                {
                    switch (tok[i])
                    {
                        case "--flash_mode": mode = tok[i + 1]; break;
                        case "--flash_freq": freq = tok[i + 1]; break;
                        case "--flash_size": size = tok[i + 1]; break;
                    }
                }
                continue;
            }

            // "<오프셋> <경로>" — 경로에 공백이 있을 수 있어 첫 토큰만 떼고 나머지를 경로로 본다.
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp <= 0) continue;
            if (!TryParseOffset(line[..sp], out uint offset)) continue;

            string path = line[(sp + 1)..].Trim().Trim('"');
            if (path.Length == 0) continue;
            entries.Add(new FlashArgEntry(offset, path));
        }

        return new FlashArgs { Entries = entries, FlashMode = mode, FlashFreq = freq, FlashSize = size };
    }

    /// <summary><c>flasher_args.json</c> 의 <c>flash_files</c> / <c>flash_settings</c> / <c>extra_esptool_args</c>.</summary>
    public static FlashArgs ParseJson(string content)
    {
        var entries = new List<FlashArgEntry>();
        string? mode = null, freq = null, size = null;
        var chip = EspChip.Unknown;

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (root.TryGetProperty("flash_files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in files.EnumerateObject())
            {
                if (!TryParseOffset(p.Name, out uint offset)) continue;
                string? path = p.Value.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                entries.Add(new FlashArgEntry(offset, path!));
            }
            // JSON 객체의 키 순서는 보장되지 않으니 오프셋 순으로 정렬해 표시를 안정화한다.
            entries.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }

        if (root.TryGetProperty("flash_settings", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            mode = Str(st, "flash_mode");
            freq = Str(st, "flash_freq");
            size = Str(st, "flash_size");
        }

        if (root.TryGetProperty("extra_esptool_args", out var extra) && extra.ValueKind == JsonValueKind.Object
            && Str(extra, "chip") is { Length: > 0 } chipName)
        {
            chip = ParseChipName(chipName);
        }

        return new FlashArgs { Entries = entries, FlashMode = mode, FlashFreq = freq, FlashSize = size, Chip = chip };

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>"esp32s3" / "ESP32-S3" 같은 표기를 모두 받아들인다.</summary>
    public static EspChip ParseChipName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return EspChip.Unknown;
        string k = name.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return k switch
        {
            "esp32" => EspChip.Esp32,
            "esp32s2" => EspChip.Esp32S2,
            "esp32s3" => EspChip.Esp32S3,
            "esp32c2" => EspChip.Esp32C2,
            "esp32c3" => EspChip.Esp32C3,
            "esp32c6" => EspChip.Esp32C6,
            "esp32h2" => EspChip.Esp32H2,
            "esp32p4" => EspChip.Esp32P4,
            _ => EspChip.Unknown,
        };
    }

    /// <summary>"0x10000" / "10000"(16진 취급) / "0X8000" 을 받는다.</summary>
    public static bool TryParseOffset(string text, out uint offset)
    {
        offset = 0;
        string s = text.Trim();
        if (s.Length == 0) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
    }
}
