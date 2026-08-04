using System.IO.Compression;
using System.Text.RegularExpressions;

namespace UartTerminal.Core.Flash;

/// <summary>플래시 대상 한 줄(Flash Download Tool 의 한 행에 대응).</summary>
public sealed record FlashItem
{
    /// <summary>bootloader / partition-table / otadata / app / storage / other</summary>
    public string Role { get; init; } = "other";

    public uint Offset { get; init; }

    /// <summary>패키지 안의 실제 파일명. 못 찾았으면 null(= 이 줄은 쓸 수 없다).</summary>
    public string? FileName { get; init; }

    public long Size { get; init; }

    /// <summary>기본 체크 여부. 표준 4종은 켜고, storage 처럼 장치 데이터를 덮을 수 있는 항목은 끈다.</summary>
    public bool Selected { get; init; }

    /// <summary>후보가 여럿일 때(예: 앱 bin 사본 2개) 사용자에게 고르게 할 목록.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    /// <summary>이 줄에 대한 설명/주의(UI 툴팁·회색 글씨).</summary>
    public string? Note { get; init; }

    public string OffsetText => $"0x{Offset:X}";
}

/// <summary>패키지(zip/폴더) 해석 결과 — 무엇을 어디에 쓸지, 칩은 무엇인지, 무엇이 위험한지.</summary>
public sealed record FlashPackage
{
    public string SourcePath { get; init; } = "";

    public EspChip Chip { get; init; } = EspChip.Unknown;

    /// <summary>칩을 무엇으로 판단했는지(사용자에게 근거를 보여주기 위함).</summary>
    public string ChipSource { get; init; } = "판별 불가";

    /// <summary>bootloader 헤더에서 읽은 SPI 설정(감지값). 기본 동작은 '바이너리 그대로(keep)'.</summary>
    public EspImageInfo? Detected { get; init; }

    public FlashArgs Args { get; init; } = FlashArgs.Empty;

    public IReadOnlyList<FlashItem> Items { get; init; } = Array.Empty<FlashItem>();

    /// <summary>진행은 가능하지만 사용자가 알아야 하는 것.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>이대로는 플래시하면 안 되는 것.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool IsUsable => Errors.Count == 0 && Items.Any(i => i.FileName is not null);
}

/// <summary>패키지 원본 추상화 — zip 과 이미 풀린 폴더를 같은 코드로 해석하기 위한 이음새(테스트도 이걸 씀).</summary>
public interface IFlashSource
{
    /// <summary>(파일명, 크기) 목록. 디렉터리 구조는 무시하고 <b>파일명</b>으로 다룬다(배포 zip 은 대개 평면).</summary>
    IReadOnlyList<(string Name, long Size)> Files { get; }

    /// <summary>파일명으로 내용을 연다(없으면 null).</summary>
    Stream? Open(string name);
}

/// <summary>zip 을 <b>풀지 않고</b> 훑는 원본(선택 화면을 먼저 보여주고, 실제 해제는 플래시 직전에).</summary>
public sealed class ZipFlashSource : IFlashSource, IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _byName;

    public ZipFlashSource(string zipPath)
    {
        _zip = ZipFile.OpenRead(zipPath);
        _byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, long)>();
        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;      // 디렉터리 엔트리
            if (e.Name.Length == 0) continue;
            // 같은 파일명이 여러 폴더에 있으면 첫 번째만(배포 zip 에선 사실상 발생하지 않는다)
            if (_byName.TryAdd(e.Name, e)) list.Add((e.Name, e.Length));
        }
        Files = list;
    }

    public IReadOnlyList<(string Name, long Size)> Files { get; }

    public Stream? Open(string name) => _byName.TryGetValue(name, out var e) ? e.Open() : null;

    public void Dispose() => _zip.Dispose();
}

/// <summary>이미 풀려 있는 폴더를 원본으로.</summary>
public sealed class DirectoryFlashSource : IFlashSource
{
    private readonly string _root;

    public DirectoryFlashSource(string root)
    {
        _root = root;
        Files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => (Path.GetFileName(p), new FileInfo(p).Length))
            .ToList();
    }

    public IReadOnlyList<(string Name, long Size)> Files { get; }

    public Stream? Open(string name)
    {
        string? hit = Directory.EnumerateFiles(_root, name, SearchOption.AllDirectories).FirstOrDefault();
        return hit is null ? null : File.OpenRead(hit);
    }
}

/// <summary>
/// 패키지 해석기 — "이 zip 을 어떤 칩에, 어느 오프셋에, 어떤 파일로 쓸지"를 결정하고 위험을 표면화한다.
///
/// 실제 배포 zip 에서 확인된 함정들을 그대로 다룬다:
///  1) <c>flash_project_args</c> 의 경로는 <b>빌드 트리 기준</b>(<c>bootloader/bootloader.bin</c>)인데 zip 은 평면 → 파일명으로 매칭
///  2) args 의 앱 파일명이 실제와 다를 수 있다(빌드명 <c>VMS.bin</c> → 배포 시 <c>OD420.bin</c> 로 rename) → 역할로 재탐색
///  3) 같은 앱의 사본이 여럿(<c>OD420.bin</c> / <c>OD420-4.0.1724.229.bin</c>) → 후보로 올리고 버전 붙은 쪽을 기본 선택
///  4) zip 에 칩 정보가 없어도 <b>bootloader 헤더의 chip_id</b> 로 확정
///  5) 같은 오프셋에 두 파일이 잡히면 오류로 막는다
/// </summary>
public static class FlashPackageAnalyzer
{
    /// <summary>ESP-IDF 관례 오프셋(args 가 없을 때의 추정용).</summary>
    private const uint PartitionTableOffset = 0x8000;
    private const uint OtaDataOffset = 0xD000;
    private const uint AppOffset = 0x10000;

    /// <summary>버전이 박힌 파일명(예: OD420-4.0.1724.229.bin)을 사본보다 우선한다 — 무엇을 구웠는지 추적 가능하니까.</summary>
    private static readonly Regex VersionLike = new(@"\d+\.\d+\.\d+", RegexOptions.Compiled);

    public static FlashPackage Analyze(IFlashSource source, string sourcePath = "", EspChip chipOverride = EspChip.Unknown)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var files = source.Files
            .Where(f => !IsMetaFile(f.Name))
            .ToList();

        // ── 1) 인자 파일(오프셋의 근거) ──────────────────────────────────────
        var args = ReadArgs(source, warnings);

        // ── 2) 칩 판별: bootloader 헤더 → flasher_args.json → 부트로더 오프셋 → override ──
        string? bootloaderName = FindByRole(files, "bootloader");
        EspImageInfo? detected = bootloaderName is null ? null : ReadHeader(source, bootloaderName);

        var chip = EspChip.Unknown;
        string chipSource = "판별 불가";
        if (detected is { Chip: not EspChip.Unknown } d)
        {
            chip = d.Chip;
            chipSource = $"{bootloaderName} 헤더(chip_id)";
        }
        else if (args.Chip != EspChip.Unknown)
        {
            chip = args.Chip;
            chipSource = "flasher_args.json";
        }

        // ── 3) 항목 만들기 ───────────────────────────────────────────────────
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = args.Entries.Count > 0
            ? BuildFromArgs(args, files, used, warnings)
            : BuildFromConventions(files, used, chip, warnings);

        // args 에 없고 후보로도 올라가지 않은 .bin 만 '위치 불명'으로 알린다.
        // (후보로 노출된 사본은 사용자가 목록에서 바로 고를 수 있으므로 경고가 아니다)
        var offered = items.SelectMany(i => i.Candidates).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var leftover in files.Where(f => IsBin(f.Name) && !used.Contains(f.Name) && !offered.Contains(f.Name)))
            warnings.Add($"{leftover.Name} 은(는) 쓸 위치를 알 수 없어 목록에 넣지 않았습니다(필요하면 직접 추가).");

        // ── 4) 검증 ──────────────────────────────────────────────────────────
        if (args.Entries.Count == 0 && items.Count == 0)
            errors.Add("플래시할 파일을 찾지 못했습니다(flash_project_args / flasher_args.json 도 없음).");

        foreach (var dup in items.Where(i => i.FileName is not null)
                                 .GroupBy(i => i.Offset).Where(g => g.Count() > 1))
        {
            errors.Add($"오프셋 0x{dup.Key:X} 에 파일이 {dup.Count()}개 잡혔습니다 " +
                       $"({string.Join(", ", dup.Select(i => i.FileName))}) — 하나만 남기세요.");
        }

        foreach (var empty in items.Where(i => i.FileName is not null && i.Size == 0))
            errors.Add($"{empty.FileName} 크기가 0입니다.");

        if (chipOverride != EspChip.Unknown && chip != EspChip.Unknown && chipOverride != chip)
        {
            warnings.Add($"선택한 칩({chipOverride.DisplayName()})이 패키지에서 판별된 칩({chip.DisplayName()})과 다릅니다 " +
                         "— 부트로더 오프셋이 맞지 않으면 부팅되지 않습니다.");
        }
        if (chipOverride != EspChip.Unknown)
        {
            chip = chipOverride;
            chipSource = "사용자 지정";
        }

        if (chip == EspChip.Unknown)
            warnings.Add("칩을 판별하지 못했습니다 — 칩을 직접 고르거나 연결된 보드에서 감지하세요.");
        else if (items.FirstOrDefault(i => i.Role == "bootloader") is { FileName: not null } bl
                 && chip.BootloaderOffset() is { } expected && bl.Offset != expected)
        {
            warnings.Add($"{chip.DisplayName()} 의 부트로더 오프셋은 0x{expected:X} 인데 패키지는 0x{bl.Offset:X} 입니다 " +
                         "— 칩이나 패키지가 맞지 않을 수 있습니다.");
        }

        return new FlashPackage
        {
            SourcePath = sourcePath,
            Chip = chip,
            ChipSource = chipSource,
            Detected = detected,
            Args = args,
            Items = items,
            Warnings = warnings,
            Errors = errors,
        };
    }

    // ── 인자 파일 ────────────────────────────────────────────────────────────

    private static FlashArgs ReadArgs(IFlashSource source, List<string> warnings)
    {
        foreach (string name in FlashArgsFile.CandidateNames)
        {
            var hit = source.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit.Name is null) continue;
            try
            {
                using var s = source.Open(hit.Name);
                if (s is null) continue;
                using var r = new StreamReader(s);
                return FlashArgsFile.Parse(hit.Name, r.ReadToEnd());
            }
            catch (Exception ex)
            {
                warnings.Add($"{hit.Name} 을(를) 읽지 못했습니다: {ex.Message}");
            }
        }
        warnings.Add("flash_project_args / flasher_args.json 이 없어 파일명 관례로 오프셋을 추정했습니다 — 값을 확인하세요.");
        return FlashArgs.Empty;
    }

    // ── 항목 구성 ────────────────────────────────────────────────────────────

    private static List<FlashItem> BuildFromArgs(FlashArgs args, List<(string Name, long Size)> files,
                                                 HashSet<string> used, List<string> warnings)
    {
        var items = new List<FlashItem>();
        foreach (var entry in args.Entries)
        {
            string role = RoleOf(entry.FileName, entry.Offset);

            // ① 파일명 그대로 매칭(zip 이 평면이므로 basename 으로)
            var hit = files.FirstOrDefault(f => string.Equals(f.Name, entry.FileName, StringComparison.OrdinalIgnoreCase));

            var candidates = Array.Empty<string>() as IReadOnlyList<string>;
            string? note = null;

            if (hit.Name is null)
            {
                // ② args 의 이름이 실제와 다른 경우(rename) → 역할로 재탐색
                var byRole = FindCandidates(files, role, used);
                if (byRole.Count == 1)
                {
                    hit = files.First(f => f.Name == byRole[0]);
                    note = $"args 의 {entry.FileName} 을(를) 찾지 못해 {hit.Name} 으로 대체";
                    warnings.Add($"0x{entry.Offset:X}: args 는 {entry.FileName} 을 가리키지만 패키지에 없어 {hit.Name} 을 씁니다.");
                }
                else if (byRole.Count > 1)
                {
                    candidates = byRole;
                    string best = PreferredCandidate(byRole);
                    hit = files.First(f => f.Name == best);
                    note = $"후보 {byRole.Count}개 — 확인 필요";
                    // args 가 가리키는 이름을 못 찾았다는 사실을 반드시 함께 알린다 — 사용자가 상황을 이해해야
                    // 후보 중 무엇을 골라야 할지 판단할 수 있다.
                    warnings.Add($"0x{entry.Offset:X}: args 는 {entry.FileName} 을 가리키지만 패키지에 없습니다. " +
                                 $"후보 {byRole.Count}개({string.Join(", ", byRole)}) 중 {best} 을 기본 선택했습니다.");
                }
                else
                {
                    warnings.Add($"0x{entry.Offset:X}: {entry.FileName} 을(를) 패키지에서 찾지 못했습니다.");
                }
            }

            if (hit.Name is not null) used.Add(hit.Name);

            items.Add(new FlashItem
            {
                Role = role,
                Offset = entry.Offset,
                FileName = hit.Name,
                Size = hit.Name is null ? 0 : hit.Size,
                Selected = hit.Name is not null && DefaultSelected(role),
                Candidates = candidates,
                Note = note ?? (hit.Name is null ? "패키지에 파일이 없습니다"
                                                 : DefaultSelected(role) ? null : "기본 해제(데이터 보호)"),
            });
        }
        return items;
    }

    /// <summary>인자 파일이 없을 때: 파일명 관례로 오프셋을 추정한다(값은 사용자가 확인해야 한다).</summary>
    private static List<FlashItem> BuildFromConventions(List<(string Name, long Size)> files,
                                                        HashSet<string> used, EspChip chip, List<string> warnings)
    {
        var items = new List<FlashItem>();
        void Add(string role, uint offset)
        {
            var cands = FindCandidates(files, role, used);
            if (cands.Count == 0) return;
            string pick = PreferredCandidate(cands);
            var f = files.First(x => x.Name == pick);
            used.Add(pick);
            items.Add(new FlashItem
            {
                Role = role,
                Offset = offset,
                FileName = pick,
                Size = f.Size,
                Selected = DefaultSelected(role),
                Candidates = cands.Count > 1 ? cands : Array.Empty<string>(),
                Note = "오프셋 추정값 — 확인 필요",
            });
        }

        Add("bootloader", chip.BootloaderOffset() ?? 0x1000u);
        Add("partition-table", PartitionTableOffset);
        Add("otadata", OtaDataOffset);
        Add("app", AppOffset);
        return items;
    }

    // ── 역할 판별 / 후보 선택 ────────────────────────────────────────────────

    /// <summary>파일명·오프셋으로 역할을 추정한다(표시 라벨과 기본 체크 여부에 쓰인다).</summary>
    public static string RoleOf(string fileName, uint offset)
    {
        string n = fileName.ToLowerInvariant();
        if (n.Contains("bootloader")) return "bootloader";
        if (n.Contains("partition")) return "partition-table";
        if (n.Contains("ota_data") || n.Contains("otadata")) return "otadata";
        if (n.Contains("storage") || n.Contains("spiffs") || n.Contains("littlefs") || n.Contains("fatfs")) return "storage";
        return offset switch
        {
            PartitionTableOffset => "partition-table",
            OtaDataOffset => "otadata",
            AppOffset => "app",
            0x0 or 0x1000 or 0x2000 => "bootloader",
            _ => "other",
        };
    }

    private static string? FindByRole(List<(string Name, long Size)> files, string role)
        => FindCandidates(files, role, used: null).FirstOrDefault();

    /// <summary>역할에 맞는 파일 후보(이미 쓰인 파일은 제외). 앱은 '남은 .bin' 이라 마지막에 걸러진다.</summary>
    private static IReadOnlyList<string> FindCandidates(List<(string Name, long Size)> files, string role,
                                                        HashSet<string>? used)
    {
        bool Free(string n) => used is null || !used.Contains(n);

        // 역할 판별은 RoleOf 하나로 통일한다(파일명 규칙이 한곳에만 있게).
        // 오프셋을 모르는 탐색이라 uint.MaxValue 를 넘기며, 이때 앱은 '아무 역할에도 안 걸린 .bin'(=other)이 된다.
        string want = role == "app" ? "other" : role;
        var hits = files.Where(f => IsBin(f.Name) && Free(f.Name) && RoleOf(f.Name, uint.MaxValue) == want);

        return hits.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>후보 중 기본 선택: 버전이 박힌 이름 우선(추적 가능), 그다음 이름이 긴 쪽.</summary>
    public static string PreferredCandidate(IReadOnlyList<string> candidates)
    {
        var versioned = candidates.Where(c => VersionLike.IsMatch(c)).ToList();
        var pool = versioned.Count > 0 ? versioned : candidates;
        return pool.OrderByDescending(c => c.Length).ThenBy(c => c, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>
    /// 기본 체크 여부. 표준 4종만 켠다 — <c>storage</c> 같은 데이터 파티션을 자동으로 덮으면
    /// 장치별 데이터(캘리브레이션 등)를 날릴 수 있다.
    /// </summary>
    public static bool DefaultSelected(string role) =>
        role is "bootloader" or "partition-table" or "otadata" or "app";

    // ── 유틸 ─────────────────────────────────────────────────────────────────

    private static EspImageInfo? ReadHeader(IFlashSource source, string name)
    {
        try
        {
            using var s = source.Open(name);
            if (s is null) return null;
            Span<byte> head = stackalloc byte[EspImageHeader.MinLength];
            int read = 0;
            while (read < head.Length)
            {
                int n = s.Read(head[read..]);
                if (n <= 0) break;
                read += n;
            }
            return read >= head.Length && EspImageHeader.TryParse(head, out var info) ? info : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBin(string name) => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    private static bool IsMetaFile(string name) =>
        FlashArgsFile.CandidateNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
}
