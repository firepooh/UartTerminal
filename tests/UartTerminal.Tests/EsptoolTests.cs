using UartTerminal.Core.Flash;

namespace UartTerminal.Tests;

/// <summary>
/// esptool 탐색·명령 생성·진행률 파싱 회귀 테스트.
/// 실제 보드나 esptool 설치 없이 돌도록 파일 시스템·프로세스는 이음새로 대체한다.
/// </summary>
public sealed class EsptoolTests
{
    // ── 명령 생성(버전별 문법) ──────────────────────────────────────────────

    private static FlashRequest Request() => new()
    {
        Port = "COM4",
        Baud = 576000,
        Chip = EspChip.Esp32S3,
        Files = new[]
        {
            (0x0u, @"C:\pkg\bootloader.bin"),
            (0x8000u, @"C:\pkg\partition-table.bin"),
            (0x10000u, @"C:\pkg\OD420-4.0.1724.229.bin"),
        },
    };

    [Fact]
    public void V4_UsesUnderscoreSyntax()
    {
        var args = EsptoolCommand.BuildWriteFlash(Request(), hyphenSyntax: false);

        Assert.Contains("write_flash", args);
        Assert.Contains("--flash_mode", args);
        Assert.DoesNotContain("write-flash", args);
        Assert.DoesNotContain("--flash-mode", args);
    }

    [Fact]
    public void V5_UsesHyphenSyntax()
    {
        // v5 에서 명령·옵션이 하이픈으로 바뀌었다 — 이걸 틀리면 실행 즉시 실패한다.
        var args = EsptoolCommand.BuildWriteFlash(Request(), hyphenSyntax: true);

        Assert.Contains("write-flash", args);
        Assert.Contains("--flash-mode", args);
        Assert.Contains("--flash-freq", args);
        Assert.Contains("--flash-size", args);
        Assert.DoesNotContain("write_flash", args);
    }

    [Fact]
    public void Args_HaveChipPortBaud_AndOffsetFilePairsInOrder()
    {
        var args = EsptoolCommand.BuildWriteFlash(Request(), hyphenSyntax: false);

        Assert.Equal("--chip", args[0]);
        Assert.Equal("esp32s3", args[1]);
        Assert.Contains("--port", args);
        Assert.Equal("COM4", args[args.IndexOf("--port") + 1]);
        Assert.Equal("576000", args[args.IndexOf("--baud") + 1]);

        // 오프셋/파일이 넘긴 순서 그대로 쌍을 이뤄야 한다.
        int i = args.IndexOf("0x0");
        Assert.Equal(@"C:\pkg\bootloader.bin", args[i + 1]);
        Assert.Equal("0x8000", args[i + 2]);
        Assert.Equal(@"C:\pkg\partition-table.bin", args[i + 3]);
        Assert.Equal("0x10000", args[i + 4]);
    }

    [Fact]
    public void KeepFlashSettings_IsDefault_MatchingDoNotChgBin()
    {
        // Flash Download Tool 의 DoNotChgBin 과 같은 동작이 기본이어야 한다
        // (헤더를 다시 쓰면 빌드 때 정한 mode/freq/size 와 달라질 수 있다).
        var args = EsptoolCommand.BuildWriteFlash(Request(), hyphenSyntax: false);
        Assert.Equal("keep", args[args.IndexOf("--flash_mode") + 1]);
        Assert.Equal("keep", args[args.IndexOf("--flash_freq") + 1]);
        Assert.Equal("keep", args[args.IndexOf("--flash_size") + 1]);
    }

    [Fact]
    public void ExplicitFlashSettings_AreUsedWhenNotKeeping()
    {
        var req = Request() with
        {
            KeepFlashSettings = false, FlashMode = "dio", FlashFreq = "80m", FlashSize = "16MB",
        };
        var args = EsptoolCommand.BuildWriteFlash(req, hyphenSyntax: true);

        Assert.Equal("dio", args[args.IndexOf("--flash-mode") + 1]);
        Assert.Equal("80m", args[args.IndexOf("--flash-freq") + 1]);
        Assert.Equal("16MB", args[args.IndexOf("--flash-size") + 1]);
        Assert.DoesNotContain("keep", args);
    }

    [Fact]
    public void UnknownChip_OmitsChipOption_SoEsptoolAutoDetects()
    {
        var args = EsptoolCommand.BuildWriteFlash(Request() with { Chip = EspChip.Unknown }, false);
        Assert.DoesNotContain("--chip", args);
    }

    [Fact]
    public void ResetOptions_AreNotPassed()
    {
        // --before/--after 는 기본값이 우리가 원하는 동작이고 표기가 버전마다 달라 실패 지점만 늘린다.
        var args = EsptoolCommand.BuildWriteFlash(Request(), hyphenSyntax: true);
        Assert.DoesNotContain("--before", args);
        Assert.DoesNotContain("--after", args);
    }

    [Fact]
    public void DisplayLine_QuotesPathsWithSpaces()
    {
        string line = EsptoolCommand.ToDisplayLine(@"C:\Program Files\esptool.exe", new[] { "--chip", "esp32s3" });
        Assert.StartsWith("\"C:\\Program Files\\esptool.exe\"", line);
        Assert.EndsWith("--chip esp32s3", line);
    }

    // ── 진행률 파싱 ─────────────────────────────────────────────────────────

    /// <summary>esptool v4 의 실제 출력 흐름(파일 2개).</summary>
    private static readonly string[] RealOutput =
    {
        "esptool.py v4.9.1",
        "Serial port COM4",
        "Connecting....",
        "Chip is ESP32-S3 (QFN56) (revision v0.2)",
        "Uploading stub...",
        "Running stub...",
        "Stub running...",
        "Changing baud rate to 576000",
        "Changed.",
        "Configuring flash size...",
        "Flash will be erased from 0x00000000 to 0x00005fff...",
        "Compressed 22426 bytes to 14172...",
        "Writing at 0x00000000... (50 %)",
        "Writing at 0x00004000... (100 %)",
        "Wrote 22426 bytes (14172 compressed) at 0x00000000 in 0.4 seconds...",
        "Hash of data verified.",
        "Compressed 8192 bytes to 31...",
        "Writing at 0x0000d000... (100 %)",
        "Wrote 8192 bytes (31 compressed) at 0x0000d000 in 0.1 seconds...",
        "Hash of data verified.",
        "Leaving...",
        "Hard resetting via RTS pin...",
    };

    [Fact]
    public void Progress_AccumulatesAcrossFiles_NeverGoingBackwards()
    {
        // 파일별 %만 쓰면 파일이 바뀔 때 0%로 되돌아 보인다 → 누적 바이트 기준이어야 한다.
        var parser = new EsptoolProgressParser(new long[] { 22426, 8192 });
        double last = 0;
        var seen = new List<double>();

        foreach (string line in RealOutput)
        {
            if (!parser.Feed(line, out var p)) continue;
            Assert.True(p.Fraction >= last - 1e-9, $"진행률이 뒤로 갔습니다: {last:P1} → {p.Fraction:P1} ({line})");
            last = p.Fraction;
            seen.Add(p.Fraction);
        }

        Assert.NotEmpty(seen);
        Assert.Equal(1.0, last, 3);            // 마지막엔 100%
        Assert.Equal(2, parser.CompletedFiles);
    }

    [Fact]
    public void Progress_FirstFileHalfway_IsWeightedByBytes()
    {
        var parser = new EsptoolProgressParser(new long[] { 1000, 3000 });

        Assert.True(parser.Feed("Writing at 0x00000000... (50 %)", out var p));
        // 1000바이트 중 50% = 500 / 전체 4000 = 12.5%
        Assert.Equal(0.125, p.Fraction, 3);
    }

    [Fact]
    public void Progress_ReportsPhaseText()
    {
        var parser = new EsptoolProgressParser(new long[] { 100 });

        Assert.True(parser.Feed("Connecting....", out var a));
        Assert.Equal("연결 중", a.Phase);
        Assert.True(parser.Feed("Writing at 0x0... (10 %)", out var b));
        Assert.Equal("쓰는 중", b.Phase);
        Assert.True(parser.Feed("Hash of data verified.", out var c));
        Assert.Equal("검증됨", c.Phase);
    }

    [Fact]
    public void Progress_IgnoresUnrelatedLines()
    {
        var parser = new EsptoolProgressParser(new long[] { 100 });
        Assert.False(parser.Feed("Serial port COM4", out _));
        Assert.False(parser.Feed("", out _));
    }

    [Fact]
    public void Progress_SurvivesUnknownFormat_WithoutThrowing()
    {
        // 출력 형식은 계약이 아니다 — 못 읽으면 진행률만 멈추고 예외는 없어야 한다.
        var parser = new EsptoolProgressParser(new long[] { 100 });
        Assert.False(parser.Feed("Writing at ??? (abc %)", out _));
        Assert.False(parser.Feed("완전히 다른 형식", out _));
    }

    [Fact]
    public void Progress_ZeroSizeFiles_DoNotDivideByZero()
    {
        var parser = new EsptoolProgressParser(Array.Empty<long>());
        parser.Feed("Writing at 0x0... (50 %)", out var p);
        Assert.InRange(p.Fraction, 0, 1);
    }

    // ── 버전 파싱 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("esptool.py v4.9.1\n4.9.1", 4)]
    [InlineData("esptool v5.3.1", 5)]
    [InlineData("esptool.py v4.12.dev3", 4)]
    [InlineData("헛소리", 0)]
    [InlineData("", 0)]
    public void Version_MajorParsing(string output, int expected)
        => Assert.Equal(expected, EsptoolLocator.ParseMajorVersion(output));

    [Fact]
    public void EsptoolInfo_SyntaxFollowsMajorVersion()
    {
        Assert.False(new EsptoolInfo("x", "v4.9.1", 4).UsesHyphenSyntax);
        Assert.True(new EsptoolInfo("x", "v5.3.1", 5).UsesHyphenSyntax);
    }

    // ── 탐색 순서 ───────────────────────────────────────────────────────────

    private static EsptoolSearch Search(params string[] existing) => new()
    {
        AppDirectory = @"C:\app",
        EspressifRoot = @"C:\home\.espressif",
        PathDirectories = new[] { @"C:\bin" },
        FileExists = p => existing.Contains(p, StringComparer.OrdinalIgnoreCase),
        ReadAllText = _ => throw new FileNotFoundException(),
        EnumerateDirectories = _ => Array.Empty<string>(),
    };

    [Fact]
    public void Candidates_PreferBundledOverPath()
    {
        string bundled = @"C:\app\tools\esptool\esptool.exe";
        string onPath = @"C:\bin\esptool.exe";
        var found = EsptoolLocator.Candidates(Search(onPath, bundled));

        Assert.Equal(bundled, found[0]);   // 번들이 먼저 — 개발환경 없는 PC 를 위해
        Assert.Contains(onPath, found);
    }

    [Fact]
    public void Candidates_UserPathWinsOverEverything()
    {
        string user = @"D:\my\esptool.exe";
        string bundled = @"C:\app\tools\esptool\esptool.exe";
        var search = Search(user, bundled) with { UserPath = user };

        Assert.Equal(user, EsptoolLocator.Candidates(search)[0]);
    }

    [Fact]
    public void Candidates_UsesSelectedIdfFromEspIdfJson()
    {
        // esp_idf.json 의 idfSelectedId 가 가리키는 python 환경의 esptool 을 먼저 써야
        // 사용자가 쓰는 IDF 와 버전이 어긋나지 않는다.
        const string json = """
        {
          "idfSelectedId": "sel",
          "idfInstalled": {
            "old": { "version": "4.4.3", "python": "C:\\home\\.espressif\\python_env\\idf4.4\\Scripts\\python.exe" },
            "sel": { "version": "5.4.2", "python": "C:\\home\\.espressif\\python_env\\idf5.4\\Scripts\\python.exe" }
          }
        }
        """;
        string selected = @"C:\home\.espressif\python_env\idf5.4\Scripts\esptool.exe";
        string other = @"C:\home\.espressif\python_env\idf4.4\Scripts\esptool.exe";

        var search = new EsptoolSearch
        {
            AppDirectory = @"C:\app",
            EspressifRoot = @"C:\home\.espressif",
            FileExists = p => p is @"C:\home\.espressif\esp_idf.json" || p == selected || p == other,
            ReadAllText = _ => json,
            EnumerateDirectories = _ => Array.Empty<string>(),
        };

        var found = EsptoolLocator.Candidates(search);
        Assert.Equal(selected, found[0]);
        Assert.Contains(other, found);
    }

    [Fact]
    public void Candidates_FallsBackToScanningPythonEnv_WhenJsonMissing()
    {
        string envA = @"C:\home\.espressif\python_env\idf5.5_py3.11_env";
        string envB = @"C:\home\.espressif\python_env\idf5.1_py3.11_env";
        string exeA = Path.Combine(envA, "Scripts", "esptool.exe");
        string exeB = Path.Combine(envB, "Scripts", "esptool.exe");

        var search = new EsptoolSearch
        {
            EspressifRoot = @"C:\home\.espressif",
            FileExists = p => p == exeA || p == exeB,
            EnumerateDirectories = d => d.EndsWith("python_env") ? new[] { envB, envA } : Array.Empty<string>(),
        };

        var found = EsptoolLocator.Candidates(search);
        Assert.Equal(exeA, found[0]); // 이름 내림차순 → 최신 환경 우선
    }

    [Fact]
    public void Candidates_EmptyWhenNothingInstalled()
        => Assert.Empty(EsptoolLocator.Candidates(Search()));

    [Fact]
    public void Resolve_SkipsCandidatesThatFailToRun()
    {
        string bad = @"C:\app\tools\esptool\esptool.exe";
        string good = @"C:\bin\esptool.exe";
        var search = Search(bad, good);

        var info = EsptoolLocator.Resolve(search, path => path == good ? "esptool v5.3.1" : null);

        Assert.NotNull(info);
        Assert.Equal(good, info!.Path);
        Assert.Equal(5, info.Major);
        Assert.True(info.UsesHyphenSyntax);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoneUsable()
    {
        var info = EsptoolLocator.Resolve(Search(@"C:\bin\esptool.exe"), _ => "쓰레기 출력");
        Assert.Null(info);
    }

    [Fact]
    public void Resolve_IgnoresProbeExceptions()
    {
        var info = EsptoolLocator.Resolve(Search(@"C:\bin\esptool.exe"), _ => throw new InvalidOperationException());
        Assert.Null(info);
    }
}
