using System.IO.Compression;
using UartTerminal.Core.Flash;

namespace UartTerminal.Tests;

/// <summary>
/// 펌웨어 패키지(zip) 해석 회귀 테스트. 실제 배포 zip(OD420-4.0.1724.229-c84f0fd.zip)에서
/// 관측한 함정을 그대로 고정한다:
///  - flash_project_args 의 경로는 빌드 트리 기준인데 zip 은 평면
///  - args 의 앱 파일명(VMS.bin)이 실제 파일명(OD420*.bin)과 다름
///  - 같은 앱의 사본이 2개(크기 동일)
///  - 칩 정보는 args 에 없고 bootloader 헤더 chip_id 로만 알 수 있음(=9 → ESP32-S3)
/// </summary>
public sealed class FlashPackageTests : IDisposable
{
    private readonly string _dir;

    public FlashPackageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uartterm-flash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 헬퍼: 실제 zip 과 같은 구조를 만든다 ────────────────────────────────────

    /// <summary>ESP 이미지 헤더(24바이트) + 더미 본문. chipId 9 = ESP32-S3.</summary>
    private static byte[] Image(int chipId, int size = 64, byte spiMode = 2, byte speedSize = 0x4F)
    {
        var b = new byte[Math.Max(size, 24)];
        b[0] = 0xE9;             // magic
        b[1] = 4;                // segment_count
        b[2] = spiMode;          // 2 = DIO
        b[3] = speedSize;        // low 0xF = 80m, high 0x4 = 16MB
        b[12] = (byte)(chipId & 0xFF);
        b[13] = (byte)(chipId >> 8);
        return b;
    }

    private const string RealArgs = """
        --flash_mode dio --flash_freq 80m --flash_size 16MB
        0x0 bootloader/bootloader.bin
        0x10000 VMS.bin
        0x8000 partition_table/partition-table.bin
        0xd000 ota_data_initial.bin
        0xc10000 storage.bin
        """;

    private string MakeZip(string name, Action<ZipArchive> fill)
    {
        string path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        fill(zip);
        return path;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name).Open();
        using var w = new StreamWriter(s);
        w.Write(text);
    }

    private static void AddBytes(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(data);
    }

    /// <summary>실제 배포 zip 재현: 평면 구조 + args 앱 이름 불일치 + 앱 사본 2개.</summary>
    private string MakeRealisticZip() => MakeZip("OD420-4.0.1724.229-c84f0fd.zip", zip =>
    {
        AddText(zip, "flash_project_args", RealArgs);
        AddBytes(zip, "bootloader.bin", Image(9, 22_426));
        AddBytes(zip, "partition-table.bin", Image(9, 3_072));
        AddBytes(zip, "ota_data_initial.bin", Image(9, 8_192));
        AddBytes(zip, "OD420.bin", Image(9, 2_186_854));
        AddBytes(zip, "OD420-4.0.1724.229.bin", Image(9, 2_186_854));
        AddBytes(zip, "storage.bin", Image(9, 1_572_864));
    });

    private static FlashPackage Analyze(string zipPath, EspChip over = EspChip.Unknown)
    {
        using var src = new ZipFlashSource(zipPath);
        return FlashPackageAnalyzer.Analyze(src, zipPath, over);
    }

    // ── 칩 판별 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Chip_IsDetectedFromBootloaderHeader()
    {
        var pkg = Analyze(MakeRealisticZip());

        // zip 에는 칩 정보가 없다 — 헤더 chip_id=9 로만 알 수 있다.
        Assert.Equal(EspChip.Esp32S3, pkg.Chip);
        Assert.Contains("bootloader.bin", pkg.ChipSource);
        Assert.Equal("ESP32-S3", pkg.Chip.DisplayName());
        Assert.Equal("esp32s3", pkg.Chip.EsptoolName());
    }

    [Fact]
    public void ChipId_Mapping_MatchesEspAppFormat()
    {
        // 0=ESP32, 2=S2, 5=C3, 9=S3, 0x0C=C2, 0x0D=C6, 0x10=H2 — 9 를 H2 로 착각하기 쉽다.
        Assert.True(EspImageHeader.TryParse(Image(0), out var a));
        Assert.Equal(EspChip.Esp32, a.Chip);
        Assert.True(EspImageHeader.TryParse(Image(9), out var b));
        Assert.Equal(EspChip.Esp32S3, b.Chip);
        Assert.True(EspImageHeader.TryParse(Image(0x10), out var c));
        Assert.Equal(EspChip.Esp32H2, c.Chip);
    }

    [Fact]
    public void Header_AlsoYieldsSpiSettings()
    {
        var pkg = Analyze(MakeRealisticZip());
        Assert.NotNull(pkg.Detected);
        Assert.Equal("dio", pkg.Detected!.Value.FlashMode);
        Assert.Equal("80m", pkg.Detected!.Value.FlashFreq);
        Assert.Equal("16MB", pkg.Detected!.Value.FlashSize);
    }

    [Fact]
    public void NonEspFile_IsNotParsedAsImage()
    {
        Assert.False(EspImageHeader.TryParse(new byte[16], out _));           // magic 없음
        Assert.False(EspImageHeader.TryParse(new byte[] { 0xE9, 1 }, out _)); // 너무 짧음
    }

    [Fact]
    public void ChipOverride_Mismatch_Warns()
    {
        var pkg = Analyze(MakeRealisticZip(), over: EspChip.Esp32);

        Assert.Equal(EspChip.Esp32, pkg.Chip);              // 사용자 지정이 최종
        Assert.Equal("사용자 지정", pkg.ChipSource);
        Assert.Contains(pkg.Warnings, w => w.Contains("다릅니다"));
        // ESP32 의 부트로더는 0x1000 인데 패키지는 0x0 → 별도 경고
        Assert.Contains(pkg.Warnings, w => w.Contains("0x1000"));
    }

    // ── 오프셋/파일 매칭 ────────────────────────────────────────────────────

    [Fact]
    public void Offsets_ComeFromArgsFile_NotGuessed()
    {
        var pkg = Analyze(MakeRealisticZip());
        var byRole = pkg.Items.ToDictionary(i => i.Role);

        Assert.Equal(0x0u, byRole["bootloader"].Offset);
        Assert.Equal(0x8000u, byRole["partition-table"].Offset);
        Assert.Equal(0xD000u, byRole["otadata"].Offset);
        Assert.Equal(0x10000u, byRole["app"].Offset);
        Assert.Equal(0xC10000u, byRole["storage"].Offset);
    }

    [Fact]
    public void BuildTreePaths_AreMatchedByFileName()
    {
        // args 는 "bootloader/bootloader.bin" 인데 zip 은 평면 — basename 으로 찾아야 한다.
        var pkg = Analyze(MakeRealisticZip());
        Assert.Equal("bootloader.bin", pkg.Items.Single(i => i.Role == "bootloader").FileName);
        Assert.Equal("partition-table.bin", pkg.Items.Single(i => i.Role == "partition-table").FileName);
    }

    [Fact]
    public void RenamedApp_FallsBackToRoleSearch_AndPrefersVersionedCopy()
    {
        // args 는 VMS.bin 을 가리키지만 zip 에는 OD420.bin / OD420-4.0.1724.229.bin 이 있다.
        var pkg = Analyze(MakeRealisticZip());
        var app = pkg.Items.Single(i => i.Role == "app");

        Assert.Equal("OD420-4.0.1724.229.bin", app.FileName);   // 버전 붙은 사본 우선
        Assert.Equal(2, app.Candidates.Count);                   // 사용자가 바꿀 수 있게 후보 노출
        Assert.Contains("OD420.bin", app.Candidates);
        Assert.Contains(pkg.Warnings, w => w.Contains("VMS.bin"));
    }

    [Fact]
    public void PreferredCandidate_PicksVersionedName()
    {
        Assert.Equal("OD420-4.0.1724.229.bin",
            FlashPackageAnalyzer.PreferredCandidate(new[] { "OD420.bin", "OD420-4.0.1724.229.bin" }));
    }

    // ── 기본 선택 정책 ──────────────────────────────────────────────────────

    [Fact]
    public void OnlyAppAndOtadata_AreCheckedByDefault()
    {
        // 일상 작업은 앱 갱신이다. bootloader/partition-table 은 구성이 바뀔 때만,
        // storage 는 장치 데이터를 덮으므로 사용자가 직접 켜야 한다.
        var pkg = Analyze(MakeRealisticZip());

        Assert.True(pkg.Items.Single(i => i.Role == "app").Selected);
        Assert.True(pkg.Items.Single(i => i.Role == "otadata").Selected);
        Assert.False(pkg.Items.Single(i => i.Role == "bootloader").Selected);
        Assert.False(pkg.Items.Single(i => i.Role == "partition-table").Selected);
        Assert.False(pkg.Items.Single(i => i.Role == "storage").Selected);
    }

    [Fact]
    public void UncheckedRows_ExplainWhy()
    {
        // 왜 안 켜져 있는지 알아야 켤 수 있다.
        var pkg = Analyze(MakeRealisticZip());

        Assert.Contains("구성이 바뀌면", pkg.Items.Single(i => i.Role == "bootloader").Note);
        Assert.Contains("데이터 보호", pkg.Items.Single(i => i.Role == "storage").Note);
        Assert.Null(pkg.Items.Single(i => i.Role == "otadata").Note);   // 켜진 줄은 조용히
    }

    // ── 검증(오류/경고) ─────────────────────────────────────────────────────

    [Fact]
    public void DuplicateOffset_IsAnError()
    {
        string zip = MakeZip("dup.zip", z =>
        {
            AddText(z, "flash_project_args", "0x10000 app_a.bin\n0x10000 app_b.bin\n");
            AddBytes(z, "app_a.bin", Image(9));
            AddBytes(z, "app_b.bin", Image(9));
        });

        var pkg = Analyze(zip);
        Assert.Contains(pkg.Errors, e => e.Contains("0x10000"));
        Assert.False(pkg.IsUsable);
    }

    [Fact]
    public void MissingFile_IsWarned_AndRowIsUnusable()
    {
        string zip = MakeZip("missing.zip", z =>
        {
            AddText(z, "flash_project_args", "0x0 bootloader/bootloader.bin\n0x8000 partition_table/partition-table.bin\n");
            AddBytes(z, "bootloader.bin", Image(9));
            // partition-table.bin 없음
        });

        var pkg = Analyze(zip);
        var pt = pkg.Items.Single(i => i.Role == "partition-table");
        Assert.Null(pt.FileName);
        Assert.False(pt.Selected);
        Assert.Contains(pkg.Warnings, w => w.Contains("partition-table.bin"));
    }

    [Fact]
    public void EmptyFile_IsAnError()
    {
        string zip = MakeZip("empty.zip", z =>
        {
            AddText(z, "flash_project_args", "0x10000 app.bin\n");
            AddBytes(z, "app.bin", Array.Empty<byte>());
        });

        Assert.Contains(Analyze(zip).Errors, e => e.Contains("크기가 0"));
    }

    [Fact]
    public void NoArgsFile_FallsBackToConventions_WithWarning()
    {
        string zip = MakeZip("noargs.zip", z =>
        {
            AddBytes(z, "bootloader.bin", Image(9));
            AddBytes(z, "partition-table.bin", Image(9));
            AddBytes(z, "myapp.bin", Image(9));
        });

        var pkg = Analyze(zip);
        Assert.Equal(EspChip.Esp32S3, pkg.Chip);
        Assert.Equal(0x0u, pkg.Items.Single(i => i.Role == "bootloader").Offset);      // S3 → 0x0
        Assert.Equal(0x8000u, pkg.Items.Single(i => i.Role == "partition-table").Offset);
        Assert.Equal(0x10000u, pkg.Items.Single(i => i.Role == "app").Offset);
        Assert.Contains(pkg.Warnings, w => w.Contains("추정"));
    }

    [Fact]
    public void FlasherArgsJson_IsAlsoSupported()
    {
        string zip = MakeZip("json.zip", z =>
        {
            AddText(z, "flasher_args.json", """
            {
              "flash_settings": { "flash_mode": "dio", "flash_size": "detect", "flash_freq": "40m" },
              "flash_files": { "0x8000": "partition_table/partition-table.bin",
                               "0x1000": "bootloader/bootloader.bin",
                               "0x10000": "OD400.bin" },
              "extra_esptool_args": { "chip": "esp32" }
            }
            """);
            AddBytes(z, "bootloader.bin", Image(0));
            AddBytes(z, "partition-table.bin", Image(0));
            AddBytes(z, "OD400.bin", Image(0));
        });

        var pkg = Analyze(zip);
        Assert.Equal(EspChip.Esp32, pkg.Chip);
        // JSON 키 순서와 무관하게 오프셋 순으로 정렬돼야 표시가 안정적이다.
        Assert.Equal(new uint[] { 0x1000, 0x8000, 0x10000 }, pkg.Items.Select(i => i.Offset).ToArray());
        Assert.Equal("detect", pkg.Args.FlashSize);
    }

    // ── 인자 파서 단위 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("0x10000", 0x10000u)]
    [InlineData("0X8000", 0x8000u)]
    [InlineData("d000", 0xD000u)]
    [InlineData("0x0", 0u)]
    public void Offset_Parsing(string text, uint expected)
    {
        Assert.True(FlashArgsFile.TryParseOffset(text, out uint v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void ArgsText_KeepsOrder_AndReadsOptions()
    {
        var args = FlashArgsFile.ParseText(RealArgs);

        Assert.Equal("dio", args.FlashMode);
        Assert.Equal("80m", args.FlashFreq);
        Assert.Equal("16MB", args.FlashSize);
        Assert.Equal(new uint[] { 0x0, 0x10000, 0x8000, 0xD000, 0xC10000 },
                     args.Entries.Select(e => e.Offset).ToArray());
        Assert.Equal("bootloader.bin", args.Entries[0].FileName); // 빌드 트리 경로 → basename
    }

    [Theory]
    [InlineData("esp32s3", EspChip.Esp32S3)]
    [InlineData("ESP32-S3", EspChip.Esp32S3)]
    [InlineData("esp32", EspChip.Esp32)]
    [InlineData("esp32c6", EspChip.Esp32C6)]
    [InlineData("헛소리", EspChip.Unknown)]
    public void ChipName_Parsing(string name, EspChip expected)
        => Assert.Equal(expected, FlashArgsFile.ParseChipName(name));

    // ── 해제(Zip Slip 방어 · 재사용) ────────────────────────────────────────

    [Fact]
    public void Extract_FlattensAndReuses()
    {
        string zip = MakeZip("ex.zip", z =>
        {
            AddText(z, "flash_project_args", RealArgs);
            AddBytes(z, "bootloader.bin", Image(9, 100));
        });
        string dest = Path.Combine(_dir, "out");

        var map = FlashExtractor.Extract(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "bootloader.bin")));
        Assert.Equal(Path.Combine(dest, "bootloader.bin"), map["bootloader.bin"]);

        // 두 번째 호출은 크기가 같으니 덮어쓰지 않는다(재사용).
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(dest, "bootloader.bin"));
        FlashExtractor.Extract(zip, dest);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dest, "bootloader.bin")));
    }

    [Fact]
    public void Extract_RejectsPathTraversal()
    {
        // zip 은 외부에서 온 파일이다 — ../ 로 대상 폴더를 벗어나려는 엔트리는 막아야 한다.
        string zip = Path.Combine(_dir, "evil.zip");
        using (var fs = File.Create(zip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("../evil.bin");
            using var s = e.Open();
            s.Write(new byte[] { 1, 2, 3 });
        }

        var ex = Assert.Throws<IOException>(() => FlashExtractor.Extract(zip, Path.Combine(_dir, "out2")));
        Assert.Contains("벗어납니다", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.bin")));
    }

    [Fact]
    public void Extract_RejectsTraversal_EvenWhenFolderStructureIsKept()
    {
        // 파일명이 중복되면 폴더 구조를 유지한다 → 이 경로에서는 평면화가 방어해 주지 않으므로
        // 경로 검사가 실제로 작동해야 한다.
        string zip = Path.Combine(_dir, "evil2.zip");
        using (var fs = File.Create(zip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (string n in new[] { "a/dup.bin", "b/dup.bin", "../escape.bin" })
            {
                using var s = z.CreateEntry(n).Open();
                s.Write(new byte[] { 1 });
            }
        }

        Assert.Throws<IOException>(() => FlashExtractor.Extract(zip, Path.Combine(_dir, "out3")));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.bin")));
    }

    [Fact]
    public void Extract_RejectsAbsolutePathEntry()
    {
        string zip = Path.Combine(_dir, "evil3.zip");
        using (var fs = File.Create(zip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var s = z.CreateEntry("C:/Windows/Temp/evil.bin").Open();
            s.Write(new byte[] { 1 });
        }

        Assert.Throws<IOException>(() => FlashExtractor.Extract(zip, Path.Combine(_dir, "out4")));
    }

    [Fact]
    public void CandidateCopy_IsNotReportedAsUnknownLocation()
    {
        // 앱 사본은 후보로 노출되므로 '위치 불명' 경고를 내면 안 된다(잡음).
        var pkg = Analyze(MakeRealisticZip());
        Assert.DoesNotContain(pkg.Warnings, w => w.Contains("위치를 알 수 없어"));
    }

    [Fact]
    public void WorkFolderName_IsStableForSameZip()
    {
        string zip = MakeZip("stable.zip", z => AddBytes(z, "a.bin", Image(9)));
        Assert.Equal(FlashExtractor.WorkFolderName(zip), FlashExtractor.WorkFolderName(zip));
        Assert.StartsWith("stable-", FlashExtractor.WorkFolderName(zip));
    }
}
