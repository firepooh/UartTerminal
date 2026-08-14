using System.IO.Compression;
using UartTerminal.Core.Flash;

namespace UartTerminal.Tests;

/// <summary>
/// 플래시 주소 파싱 계약. 잘못된 주소로 구우면 장비가 손상되므로 관대한 추측 없이
/// 정확히 파싱되는 것만 통과해야 한다.
/// </summary>
public sealed class FlashAddressTests
{
    [Theory]
    [InlineData("0xD90000", 0xD90000u)]
    [InlineData("0xd90000", 0xD90000u)]
    [InlineData("4096", 4096u)]
    [InlineData("0x0", 0u)]
    [InlineData(" 0x1000 ", 0x1000u)]
    public void TryParse_HexAndDecimal(string input, uint expected)
    {
        Assert.True(FlashAddress.TryParse(input, out uint addr));
        Assert.Equal(expected, addr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0x")]
    [InlineData("0xGG")]
    [InlineData("-16")]
    [InlineData("D90000")]   // hex 인데 0x 없음 - 십진도 아니므로 거부(추측하지 않는다)
    public void TryParse_RejectsAmbiguousInput(string input)
    {
        Assert.False(FlashAddress.TryParse(input, out _));
    }
}

/// <summary>영역 굽기 소스(bin/zip) 해석 계약. http 다운로드는 네트워크라 여기서 검사하지 않는다.</summary>
public sealed class RegionSourceResolverTests : IDisposable
{
    private readonly string _dir;

    public RegionSourceResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uart-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("http://example.test/a.zip", true)]
    [InlineData("https://example.test/a.bin", true)]
    [InlineData(@"C:\files\a.bin", false)]
    [InlineData("httpx://nope", false)]
    public void IsUrl_DetectsHttpSchemes(string input, bool expected)
    {
        Assert.Equal(expected, RegionSourceResolver.IsUrl(input));
    }

    /// <summary>bin 파일은 그대로 그 파일 하나 — 복사도 압축 해제도 없다.</summary>
    [Fact]
    public void ResolveBins_PlainBin_ReturnsItself()
    {
        string bin = Path.Combine(_dir, "image.bin");
        File.WriteAllBytes(bin, new byte[] { 1, 2, 3 });

        var result = RegionSourceResolver.ResolveBins(bin, Path.Combine(_dir, "work"));
        Assert.Equal(new[] { bin }, result);
    }

    /// <summary>zip 이면 풀어서 *.bin 만 — 문서·체크섬 파일이 섞여 있어도 목록에 안 나온다.</summary>
    [Fact]
    public void ResolveBins_Zip_ExtractsAndFiltersBins()
    {
        string zip = Path.Combine(_dir, "pkg.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(z, "data.bin", new byte[] { 1 });
            WriteEntry(z, "notes.txt", new byte[] { 2 });
            WriteEntry(z, "second.bin", new byte[] { 3 });
        }

        var result = RegionSourceResolver.ResolveBins(zip, Path.Combine(_dir, "work"));
        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.EndsWith(".bin", p, StringComparison.OrdinalIgnoreCase));
        Assert.All(result, p => Assert.True(File.Exists(p)));
    }

    /// <summary>zip 판정은 확장자가 아니라 매직 넘버 — 내려받은 파일은 이름을 믿을 수 없다.</summary>
    [Fact]
    public void ResolveBins_ZipWithBinExtension_StillExtracted()
    {
        string disguised = Path.Combine(_dir, "actually-a-zip.bin");
        using (var z = ZipFile.Open(disguised, ZipArchiveMode.Create))
            WriteEntry(z, "inner.bin", new byte[] { 7 });

        var result = RegionSourceResolver.ResolveBins(disguised, Path.Combine(_dir, "work"));
        Assert.Single(result);
        Assert.Equal("inner.bin", Path.GetFileName(result[0]));
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(content);
    }
}
