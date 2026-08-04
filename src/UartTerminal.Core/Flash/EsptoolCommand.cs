using System.Text;
using System.Text.RegularExpressions;

namespace UartTerminal.Core.Flash;

/// <summary>플래시 요청 — 무엇을 어디에 어떤 조건으로 쓸지.</summary>
public sealed record FlashRequest
{
    public required string Port { get; init; }
    public required int Baud { get; init; }
    public EspChip Chip { get; init; } = EspChip.Unknown;

    /// <summary>(오프셋, 파일 전체 경로) — 넘긴 순서대로 기록된다.</summary>
    public required IReadOnlyList<(uint Offset, string File)> Files { get; init; }

    /// <summary>
    /// true 면 <c>--flash_mode/freq/size keep</c> — 바이너리 헤더를 그대로 둔다.
    /// Espressif Flash Download Tool 의 <c>DoNotChgBin</c> 체크와 같은 동작이며, 기본값으로 둔다
    /// (헤더를 다시 쓰면 빌드 때 정한 설정과 달라질 수 있다).
    /// </summary>
    public bool KeepFlashSettings { get; init; } = true;

    /// <summary>KeepFlashSettings=false 일 때만 사용.</summary>
    public string? FlashMode { get; init; }
    public string? FlashFreq { get; init; }
    public string? FlashSize { get; init; }

    /// <summary>총 바이트(진행률 계산용).</summary>
    public long TotalBytes(Func<string, long> sizeOf) => Files.Sum(f => sizeOf(f.File));
}

/// <summary>
/// esptool 명령행 생성기. v4 와 v5 의 문법 차이를 한곳에서 흡수한다.
/// <code>
/// v4: esptool --chip esp32s3 --port COM4 --baud 576000 write_flash --flash_mode keep … 0x0 boot.bin
/// v5: esptool --chip esp32s3 --port COM4 --baud 576000 write-flash --flash-mode keep … 0x0 boot.bin
/// </code>
/// <c>--before/--after</c> 는 <b>일부러 넘기지 않는다</b> — 기본값이 우리가 원하는
/// default_reset/hard_reset 이고, 이 값들의 표기도 버전에 따라 달라 실패 지점만 늘어난다.
/// </summary>
public static class EsptoolCommand
{
    public static List<string> BuildWriteFlash(FlashRequest request, bool hyphenSyntax)
    {
        string cmd = hyphenSyntax ? "write-flash" : "write_flash";
        string optMode = hyphenSyntax ? "--flash-mode" : "--flash_mode";
        string optFreq = hyphenSyntax ? "--flash-freq" : "--flash_freq";
        string optSize = hyphenSyntax ? "--flash-size" : "--flash_size";

        var args = new List<string>();

        if (request.Chip != EspChip.Unknown)
        {
            args.Add("--chip");
            args.Add(request.Chip.EsptoolName());
        }
        args.Add("--port");
        args.Add(request.Port);
        args.Add("--baud");
        args.Add(request.Baud.ToString());

        args.Add(cmd);

        if (request.KeepFlashSettings)
        {
            args.AddRange(new[] { optMode, "keep", optFreq, "keep", optSize, "keep" });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.FlashMode)) { args.Add(optMode); args.Add(request.FlashMode!); }
            if (!string.IsNullOrWhiteSpace(request.FlashFreq)) { args.Add(optFreq); args.Add(request.FlashFreq!); }
            if (!string.IsNullOrWhiteSpace(request.FlashSize)) { args.Add(optSize); args.Add(request.FlashSize!); }
        }

        foreach (var (offset, file) in request.Files)
        {
            args.Add($"0x{offset:X}");
            args.Add(file);
        }

        return args;
    }

    /// <summary>사람에게 보여주거나 로그에 남길 한 줄 명령(공백 포함 경로는 인용).</summary>
    public static string ToDisplayLine(string exePath, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(exePath));
        foreach (string a in args)
        {
            sb.Append(' ');
            sb.Append(Quote(a));
        }
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.Contains(' ') || s.Contains('\t') ? $"\"{s}\"" : s;
}

/// <summary>진행률 한 조각.</summary>
public readonly record struct FlashProgress(double Fraction, string Phase, int FileIndex);

/// <summary>
/// esptool 표준출력을 진행률로 바꾸는 파서.
///
/// 파일별 퍼센트만 믿으면 전체 진행률이 파일마다 0%로 되돌아가 보이므로,
/// <b>완료된 파일의 바이트 합 + 현재 파일의 퍼센트</b>로 전체를 계산한다.
/// 출력 형식은 계약이 아니므로(버전에 따라 바뀔 수 있다) 못 읽으면 진행률만 멈추고
/// 로그는 그대로 보여주는 것이 이 클래스의 방침이다.
/// </summary>
public sealed class EsptoolProgressParser
{
    // "Writing at 0x0001a000... (25 %)"  — 주소는 파일 시작이 아니라 진행 중 위치다.
    private static readonly Regex WritingPattern =
        new(@"Writing at 0x[0-9a-fA-F]+\.*\s*\((\d+)\s*%\)", RegexOptions.Compiled);

    // "Wrote 2186854 bytes (1234567 compressed) at 0x00010000 in 12.3 seconds..."
    private static readonly Regex WrotePattern =
        new(@"Wrote\s+\d+\s+bytes.*?at\s+0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    private readonly long[] _sizes;
    private readonly long _total;
    private int _index;
    private int _percent;
    private string _phase = "준비 중";

    public EsptoolProgressParser(IReadOnlyList<long> fileSizes)
    {
        _sizes = fileSizes.ToArray();
        _total = _sizes.Sum();
        if (_total <= 0) _total = 1; // 0 나눗셈 방지
    }

    public string Phase => _phase;

    /// <summary>완료로 간주한 파일 수(검증/테스트용).</summary>
    public int CompletedFiles => _index;

    /// <summary>한 줄을 먹이고, 진행 상황이 바뀌면 true 를 반환한다.</summary>
    public bool Feed(string line, out FlashProgress progress)
    {
        progress = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        bool changed = false;

        if (WritingPattern.Match(line) is { Success: true } w)
        {
            if (int.TryParse(w.Groups[1].Value, out int pct))
            {
                _percent = Math.Clamp(pct, 0, 100);
                _phase = "쓰는 중";
                changed = true;
            }
        }
        else if (WrotePattern.IsMatch(line))
        {
            // 한 파일 완료 → 다음 파일로. 퍼센트는 리셋.
            if (_index < _sizes.Length) _index++;
            _percent = 0;
            _phase = _index >= _sizes.Length ? "검증/마무리" : "쓰는 중";
            changed = true;
        }
        else if (UpdatePhase(line))
        {
            changed = true;
        }

        if (!changed) return false;

        long done = 0;
        for (int i = 0; i < _index && i < _sizes.Length; i++) done += _sizes[i];
        long current = _index < _sizes.Length ? _sizes[_index] : 0;
        double fraction = (done + current * (_percent / 100.0)) / _total;

        progress = new FlashProgress(Math.Clamp(fraction, 0, 1), _phase, _index);
        return true;
    }

    /// <summary>진행률은 그대로 두고 단계 문구만 갱신하는 줄들.</summary>
    private bool UpdatePhase(string line)
    {
        string? phase = line switch
        {
            var l when l.Contains("Connecting", StringComparison.OrdinalIgnoreCase) => "연결 중",
            var l when l.Contains("Chip is", StringComparison.OrdinalIgnoreCase) => "칩 확인",
            var l when l.Contains("Uploading stub", StringComparison.OrdinalIgnoreCase)
                       || l.Contains("Running stub", StringComparison.OrdinalIgnoreCase) => "스텁 로딩",
            var l when l.Contains("Changing baud", StringComparison.OrdinalIgnoreCase) => "속도 변경",
            var l when l.Contains("Configuring flash size", StringComparison.OrdinalIgnoreCase) => "플래시 설정",
            var l when l.Contains("Erasing", StringComparison.OrdinalIgnoreCase)
                       || l.Contains("will be erased", StringComparison.OrdinalIgnoreCase) => "지우는 중",
            var l when l.Contains("Hash of data verified", StringComparison.OrdinalIgnoreCase) => "검증됨",
            var l when l.Contains("Leaving", StringComparison.OrdinalIgnoreCase) => "마무리",
            var l when l.Contains("resetting", StringComparison.OrdinalIgnoreCase) => "리셋",
            _ => null,
        };

        if (phase is null || phase == _phase) return false;
        _phase = phase;
        return true;
    }
}
