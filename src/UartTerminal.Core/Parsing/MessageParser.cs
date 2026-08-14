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
    /// <summary>이 줄에 정의된 키가 하나라도 있는지(파싱 전 빠른 필터 — 수신마다 불린다).</summary>
    public static bool ContainsAnyKey(string line, IReadOnlyDictionary<string, MessageSpec> specs)
    {
        foreach (var key in specs.Keys)
            if (FindBlockStart(line, key) >= 0) return true;
        return false;
    }

    /// <summary>한 줄에서 정의된 모든 블록을 찾아 순서대로 해석한다(없으면 빈 목록).</summary>
    public static IReadOnlyList<ParsedBlock> ParseLine(string line, IReadOnlyDictionary<string, MessageSpec> specs)
    {
        // 블록의 등장 순서(라인 내 위치)대로 정렬해 화면과 같은 순서로 보여 준다.
        var found = new List<(int At, MessageSpec Spec)>();
        foreach (var spec in specs.Values)
        {
            int at = FindBlockStart(line, spec.Key);
            if (at >= 0) found.Add((at, spec));
        }
        if (found.Count == 0) return Array.Empty<ParsedBlock>();
        found.Sort((a, b) => a.At.CompareTo(b.At));

        var blocks = new List<ParsedBlock>(found.Count);
        foreach (var (at, spec) in found)
        {
            string body = ExtractBody(line, at + spec.Key.Length + 1);
            blocks.Add(ParseBlock(spec, body));
        }
        return blocks;
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
}
