using System.Globalization;

namespace UartTerminal.Core.Parsing;

/// <summary>파싱된 필드 하나: 정의된 이름 + 원시 값 + (해석이 있으면) 표시 값.</summary>
public sealed record ParsedField(string Name, string Raw, string? Decoded, string? Unit);

/// <summary>한 줄에서 발견된 메시지 블록 하나(예: "T5=…" 구간).</summary>
public sealed record ParsedBlock
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ParsedField> Fields { get; init; }

    /// <summary>정의보다 값이 적을 때 부족한 개수(펌웨어 버전 차이 — 오류가 아니라 표시할 사실).</summary>
    public int MissingCount { get; init; }
}

/// <summary>
/// "KEY=v_v_v&amp;KEY2=…" 형태의 장비→서버 보고 라인을 필드 정의(<see cref="MessageSpec"/>)로 해석한다.
///
/// 엔진은 <b>프로토콜을 모른다</b> — 어떤 키가 있고 각 필드가 무엇인지는 전부 사용자 정의
/// 파일(parsers.json)이 결정한다. 값이 정의보다 많으면 남는 값을 "#n" 이름으로 그대로 보여주고,
/// 적으면 부족한 개수를 센다(펌웨어와 정의 파일의 버전이 어긋나는 것은 정상 상황이다).
/// </summary>
public static class MessageParser
{
    /// <summary>이 줄에 정의된 블록이 하나라도 있는지(파싱 전 빠른 필터 — 수신마다 불린다).</summary>
    public static bool ContainsAnyKey(string line, IReadOnlyDictionary<string, MessageSpec> specs,
        string? prevLine = null)
    {
        foreach (var spec in specs.Values)
            if (FindBlock(line, prevLine, spec) is not null) return true;
        return false;
    }

    /// <summary>
    /// 한 줄에서 정의된 모든 블록을 찾아 순서대로 해석한다(없으면 빈 목록).
    /// <paramref name="prevLine"/> 은 afterLine 정의용 — 헤더 다음 줄에 값이 오는 콘솔 출력.
    /// </summary>
    public static IReadOnlyList<ParsedBlock> ParseLine(string line, IReadOnlyDictionary<string, MessageSpec> specs,
        string? prevLine = null)
    {
        // 블록의 등장 순서(라인 내 위치)대로 정렬해 화면과 같은 순서로 보여 준다.
        // 목록은 첫 매칭에서야 만든다 — 이 함수는 수신되는 <b>모든</b> 줄에 대해 불리고
        // 그중 대부분은 매칭이 없다(빈 List 를 줄마다 버리지 않게).
        List<(int At, string Body, MessageSpec Spec)>? found = null;
        foreach (var spec in specs.Values)
            if (FindBlock(line, prevLine, spec) is { } hit)
                (found ??= new()).Add((hit.At, hit.Body, spec));
        if (found is null) return Array.Empty<ParsedBlock>();
        found.Sort((a, b) => a.At.CompareTo(b.At));

        var blocks = new List<ParsedBlock>(found.Count);
        foreach (var (_, body, spec) in found)
            blocks.Add(ParseBlock(spec, body));
        return blocks;
    }

    /// <summary>
    /// 여러 줄 텍스트(터미널 선택 등)를 줄 단위로 훑어 발견한 블록을 전부 모은다 —
    /// afterLine 정의가 줄 경계에 걸리므로 통짜 문자열로는 안 된다.
    /// </summary>
    public static IReadOnlyList<ParsedBlock> ParseText(string text, IReadOnlyDictionary<string, MessageSpec> specs)
    {
        var blocks = new List<ParsedBlock>();
        string? prev = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            blocks.AddRange(ParseLine(line, specs, prev));
            prev = line;
        }
        return blocks;
    }

    /// <summary>
    /// 정의 방식에 따라 블록 위치와 본문을 찾는다: afterLine(직전 줄 매치 → 현재 줄 전체)
    /// → match(정규식, 그룹 1 또는 매치 전체) → 기본("KEY=" 탐지).
    /// </summary>
    private static (int At, string Body)? FindBlock(string line, string? prevLine, MessageSpec spec)
    {
        if (spec.AfterRegex is { } after)
        {
            string body = line.Trim();
            return prevLine is not null && body.Length > 0 && after.IsMatch(prevLine) ? (0, body) : null;
        }
        if (spec.MatchRegex is { } re)
        {
            var m = re.Match(line);
            if (!m.Success) return null;
            string body = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
            return (m.Index, body);
        }
        int at = FindBlockStart(line, spec.Key);
        return at >= 0 ? (at, ExtractBody(line, at + spec.Key.Length + 1)) : null;
    }

    /// <summary>
    /// "KEY=" 의 시작 위치. 앞 글자가 영숫자면 다른 토큰의 꼬리다("XT5=" 는 T5 가 아니다).
    /// 같은 키가 여러 번 있으면 첫 번째만 본다(실제 트래픽에서 그런 라인은 재전송 덤프뿐).
    /// </summary>
    private static int FindBlockStart(string line, string key)
    {
        int from = 0;
        while (true)
        {
            int at = line.IndexOf(key + "=", from, StringComparison.Ordinal);
            if (at < 0) return -1;
            if (at == 0 || !char.IsLetterOrDigit(line[at - 1])) return at;
            from = at + 1;
        }
    }

    /// <summary>블록 본문: '&amp;'(다음 블록) 또는 공백/제어문자(로그 꼬리) 앞까지.</summary>
    private static string ExtractBody(string line, int start)
    {
        int end = start;
        while (end < line.Length && line[end] != '&' && !char.IsWhiteSpace(line[end]))
            end++;
        return line[start..end];
    }

    private static ParsedBlock ParseBlock(MessageSpec spec, string body)
    {
        string[] values = body.Split(spec.Separator, StringSplitOptions.None);
        var fields = new List<ParsedField>(Math.Max(values.Length, spec.Fields.Count));

        for (int i = 0; i < values.Length; i++)
        {
            FieldSpec? f = i < spec.Fields.Count ? spec.Fields[i] : null;
            // 정의 밖의 값은 "#순번" 으로 그대로 노출 — 잘라 버리면 "정의가 낡았다" 는 사실이 안 보인다.
            string name = f?.Name is { Length: > 0 } n ? n : $"#{i + 1}";
            string? decoded = Decode(f, values[i]);
            // enum 라벨("없음"·"미지원")에 단위를 붙이면 "없음 psi" 가 된다 — 숫자가 아닌 해석엔 단위를 뗀다.
            string? unit = decoded is not null
                && !double.TryParse(decoded, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? null : f?.Unit;
            fields.Add(new ParsedField(name, values[i], decoded, unit));
        }

        return new ParsedBlock
        {
            Key = spec.Key,
            Name = spec.Name,
            Fields = fields,
            MissingCount = Math.Max(0, spec.Fields.Count - values.Length),
        };
    }

    /// <summary>해석 값(없으면 null → 원시 값만 표시). 해석 실패도 null — 원시 값이 항상 남는다.</summary>
    private static string? Decode(FieldSpec? f, string raw)
    {
        if (f is null || raw.Length == 0) return null;

        if (f.Enum is { } map && map.TryGetValue(raw, out string? label))
            return label;

        if (f.Subfields is { Count: > 0 } subs && DecodeSubfields(f, subs, raw) is { } parts)
            return parts;

        if (f.Bits is { Count: > 0 } bits && DecodeBits(f, bits, raw) is { } names)
            return names;

        if (f.Format == "datetime" && raw.Length == 14
            && DateTime.TryParseExact(raw, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");

        if (f.Format == "epoch" && long.TryParse(raw, out long sec) && sec is > 0 and < 4_102_444_800)
            return DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        if (f.Scale is { } scale
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
        {
            double v = num * scale;
            // 배율 결과는 유효자리만(0.1 배율에 12.000 이 나오지 않게)
            return v.ToString(Math.Abs(v % 1) < 1e-9 ? "0" : "0.###", CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>
    /// 켜진 비트의 라벨을 비트 번호 순으로 나열한다. 값 0·파싱 실패·이름 있는 비트가 하나도
    /// 안 켜졌으면 null(원시 값만 표시). 이름 없는 비트는 조용히 무시하지 않고 <c>b{n}</c> 으로
    /// 남긴다 — 정의에 없는 비트가 켜졌다는 것 자체가 봐야 할 정보다.
    /// </summary>
    private static string? DecodeBits(FieldSpec f, Dictionary<string, string> bits, string raw)
    {
        if (!TryParseValue(raw, f.Radix, out long value) || value <= 0) return null;

        var names = new List<string>();
        bool anyNamed = false;
        for (int bit = 0; bit < 63; bit++)
        {
            if ((value & (1L << bit)) == 0) continue;
            if (bits.TryGetValue(bit.ToString(CultureInfo.InvariantCulture), out string? name))
            {
                names.Add(name);
                anyNamed = true;
            }
            else
            {
                names.Add($"b{bit}");
            }
        }
        return anyNamed ? string.Join(", ", names) : null;
    }

    /// <summary>
    /// 마스크 비트 묶음 해석: 설정 워드에서 하위 필드마다 (값 &amp; 마스크) &gt;&gt; 시프트를 뽑아
    /// "이름=라벨" 로 잇는다. enum 있는 하위 필드는 0 도 표시(기본값도 정보다), 없는 것은
    /// 0 이 아닐 때만 — 단일 비트는 이름만(플래그). 해석할 것이 없으면 null(원시 값만 표시).
    /// </summary>
    private static string? DecodeSubfields(FieldSpec f, List<SubfieldSpec> subs, string raw)
    {
        if (!TryParseValue(raw, f.Radix, out long value)) return null;

        var parts = new List<string>();
        foreach (var s in subs)
        {
            if (!TryParseValue(s.Mask, 16, out long mask) || mask == 0) continue;
            int shift = System.Numerics.BitOperations.TrailingZeroCount((ulong)mask);
            long sub = (long)(((ulong)(value & mask)) >> shift);

            if (s.Enum is { } map)
                parts.Add($"{s.Name}={(map.TryGetValue(sub.ToString(CultureInfo.InvariantCulture), out string? lb) ? lb : sub.ToString(CultureInfo.InvariantCulture))}");
            else if (sub != 0)
                parts.Add((mask & (mask - 1)) == 0 ? s.Name : $"{s.Name}={sub}");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>
    /// 진법을 존중하는 정수 파싱. "0x" 접두는 진법 설정과 무관하게 hex 로 받는다 —
    /// 펌웨어가 %d·%X·0x%x 를 섞어 찍는 것이 현실이다.
    /// </summary>
    private static bool TryParseValue(string raw, int? radix, out long value)
    {
        string s = raw.Trim();
        bool hex = radix == 16;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { hex = true; s = s[2..]; }
        return hex
            ? long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
