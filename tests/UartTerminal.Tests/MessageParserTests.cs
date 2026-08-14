using UartTerminal.Core.Parsing;

namespace UartTerminal.Tests;

/// <summary>
/// 메시지 파싱 엔진 계약. 엔진은 프로토콜을 모른다 — 여기서도 실제 장비 프로토콜이 아니라
/// <b>중립적인 가짜 정의</b>로 검사한다(필드 정의는 사용자 파일(parsers.json)의 자산이다).
/// </summary>
public sealed class MessageParserTests
{
    private static Dictionary<string, MessageSpec> Specs(params MessageSpec[] specs)
        => specs.ToDictionary(s => s.Key, s => s, StringComparer.Ordinal);

    private static MessageSpec S1 => new()
    {
        Key = "S1",
        Name = "status",
        Fields = new List<FieldSpec>
        {
            new() { Name = "id" },
            new() { Name = "time", Format = "datetime" },
            new() { Name = "volt", Unit = "V", Scale = 0.1 },
            new() { Name = "mode", Enum = new() { ["0"] = "idle", ["1"] = "run" } },
        },
    };

    private static MessageSpec H2 => new()
    {
        Key = "H2",
        Name = "header",
        Fields = new List<FieldSpec>
        {
            new() { Name = "id" },
            new() { Name = "sent", Format = "epoch" },
        },
    };

    // ── 블록 탐지 ───────────────────────────────────────────────────────────

    /// <summary>로그 접두사(타임스탬프·태그) 뒤에 블록이 있어도 찾는다 — 실제 라인은 항상 그렇다.</summary>
    [Fact]
    public void FindsBlock_AfterLogPrefix()
    {
        var blocks = MessageParser.ParseLine(
            "I (02:33:53.126) socket: Sent > GET /?S1=dev7_20260814023452_120_1", Specs(S1));

        Assert.Single(blocks);
        Assert.Equal("S1", blocks[0].Key);
        Assert.Equal("dev7", blocks[0].Fields[0].Raw);
    }

    /// <summary>"XS1=" 은 S1 블록이 아니다 — 앞 글자가 영숫자면 다른 토큰의 꼬리다.</summary>
    [Fact]
    public void DoesNotMatch_KeyInsideAnotherToken()
    {
        Assert.Empty(MessageParser.ParseLine("XS1=a_b_c", Specs(S1)));
        Assert.False(MessageParser.ContainsAnyKey("XS1=a_b_c", Specs(S1)));
    }

    /// <summary>'&amp;' 로 이어진 여러 블록은 라인에 등장한 순서대로 나온다.</summary>
    [Fact]
    public void MultipleBlocks_InLineOrder()
    {
        var blocks = MessageParser.ParseLine("GET /?H2=dev7_1786674892&S1=dev7_20260814023452_120_0",
                                             Specs(S1, H2));
        Assert.Equal(2, blocks.Count);
        Assert.Equal("H2", blocks[0].Key);   // 정의 순서가 아니라 라인 순서
        Assert.Equal("S1", blocks[1].Key);
    }

    /// <summary>블록 본문은 다음 '&amp;' 또는 공백 앞에서 끝난다(HTTP 뒤에 붙는 꼬리 무시).</summary>
    [Fact]
    public void Body_StopsAtAmpersandOrWhitespace()
    {
        var blocks = MessageParser.ParseLine("S1=a_b_c_d HTTP/1.1", Specs(S1));
        Assert.Equal(4, blocks[0].Fields.Count);
        Assert.Equal("d", blocks[0].Fields[3].Raw);
    }

    // ── 값 해석 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Decodes_Enum_Scale_Datetime()
    {
        var b = MessageParser.ParseLine("S1=dev7_20260814023452_120_1", Specs(S1))[0];

        Assert.Equal("2026-08-14 02:34:52", b.Fields[1].Decoded);
        Assert.Equal("12", b.Fields[2].Decoded);          // 120 × 0.1 — 12.000 이 아니라 12
        Assert.Equal("V", b.Fields[2].Unit);
        Assert.Equal("run", b.Fields[3].Decoded);
    }

    /// <summary>enum 에 없는 값·형식이 안 맞는 값은 해석하지 않는다(원시 값이 항상 남는다).</summary>
    [Fact]
    public void UnknownValues_KeepRawOnly()
    {
        var b = MessageParser.ParseLine("S1=dev7_NOTATIME_abc_9", Specs(S1))[0];
        Assert.Null(b.Fields[1].Decoded);
        Assert.Null(b.Fields[2].Decoded);
        Assert.Null(b.Fields[3].Decoded);
        Assert.Equal("9", b.Fields[3].Raw);
    }

    [Fact]
    public void Epoch_DecodesToLocalTime()
    {
        var b = MessageParser.ParseLine("H2=dev7_1786674892", Specs(H2))[0];
        Assert.NotNull(b.Fields[1].Decoded);
        Assert.StartsWith("2026-", b.Fields[1].Decoded);   // 로컬 시간대와 무관하게 연도는 같다
    }

    // ── 정의와 값 개수가 어긋날 때(펌웨어 버전 차이 — 정상 상황) ─────────────

    /// <summary>정의보다 값이 많으면 남는 값을 "#n" 으로 그대로 노출한다 — 잘라 버리면 정의가 낡은 게 안 보인다.</summary>
    [Fact]
    public void ExtraValues_AppearAsIndexedFields()
    {
        var b = MessageParser.ParseLine("S1=a_20260814023452_120_1_EXTRA1_EXTRA2", Specs(S1))[0];
        Assert.Equal(6, b.Fields.Count);
        Assert.Equal("#5", b.Fields[4].Name);
        Assert.Equal("EXTRA1", b.Fields[4].Raw);
    }

    [Fact]
    public void MissingValues_AreCounted()
    {
        var b = MessageParser.ParseLine("S1=a_20260814023452", Specs(S1))[0];
        Assert.Equal(2, b.Fields.Count);
        Assert.Equal(2, b.MissingCount);
    }

    /// <summary>빈 필드("__")는 빈 값으로 자리 보전 — 위치 기반 프로토콜에서 자리가 밀리면 전부 틀린다.</summary>
    [Fact]
    public void EmptyFields_PreservePosition()
    {
        var b = MessageParser.ParseLine("S1=a__120_0", Specs(S1))[0];
        Assert.Equal("", b.Fields[1].Raw);
        Assert.Equal("120", b.Fields[2].Raw);
    }
}

/// <summary>parsers.json 로더 계약.</summary>
public sealed class ParserStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ParserStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uart-parse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "parsers.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoFile_EmptyWithoutError()
    {
        var s = new ParserStore(_path);
        s.Load();
        Assert.Empty(s.ByKey);
        Assert.Null(s.LastError);   // 파일 없음은 오류가 아니라 '기능 미사용'
        Assert.False(s.FileExists);
    }

    [Fact]
    public void Load_ValidFile_IndexesByKey()
    {
        File.WriteAllText(_path, """
            { "schemaVersion": 1, "messages": [
                { "key": "S1", "name": "status", "fields": [ { "name": "id" }, { "name": "volt", "unit": "V", "scale": 0.1 } ] }
            ] }
            """);
        var s = new ParserStore(_path);
        s.Load();
        Assert.Null(s.LastError);
        Assert.Equal("status", s.ByKey["S1"].Name);
        Assert.Equal(0.1, s.ByKey["S1"].Fields[1].Scale);
    }

    /// <summary>손상 파일은 빈 정의 + 사유 — 패널이 죽는 대신 안내가 뜨게.</summary>
    [Fact]
    public void Load_CorruptFile_ReportsError()
    {
        File.WriteAllText(_path, "{ not json ");
        var s = new ParserStore(_path);
        s.Load();
        Assert.Empty(s.ByKey);
        Assert.NotNull(s.LastError);
    }

    /// <summary>Items 는 파일 순서를 지킨다 — 필터 체크박스가 이 순서로 나열된다(자주 쓰는 키를 앞에 두는 관례).</summary>
    [Fact]
    public void Items_PreserveFileOrder()
    {
        File.WriteAllText(_path, """
            { "schemaVersion": 1, "messages": [
                { "key": "Z9", "fields": [] },
                { "key": "A1", "fields": [] },
                { "key": "M5", "fields": [] }
            ] }
            """);
        var s = new ParserStore(_path);
        s.Load();
        Assert.Equal(new[] { "Z9", "A1", "M5" }, s.Items.Select(i => i.Key));
    }

    /// <summary>키 하나만 매칭해도 라인의 위치가 아니라 '어느 키가 켜져 있나' 로 갈린다 — T1 은 T12 의 접두사지만 "T1=" 리터럴 매칭이라 섞이지 않는다.</summary>
    [Fact]
    public void PrefixKeys_DoNotCrossMatch()
    {
        var t1 = new MessageSpec { Key = "T1", Name = "a", Fields = new() { new() { Name = "id" } } };
        var specs = new Dictionary<string, MessageSpec>(StringComparer.Ordinal) { ["T1"] = t1 };

        Assert.Empty(MessageParser.ParseLine("T12=abc_def", specs));   // T12 라인에서 T1 오검출 없음
        Assert.Single(MessageParser.ParseLine("T1=abc", specs));
    }

    [Fact]
    public void Load_NewerSchema_RefusesWithReason()
    {
        File.WriteAllText(_path, """{ "schemaVersion": 99, "messages": [ { "key": "S1", "fields": [] } ] }""");
        var s = new ParserStore(_path);
        s.Load();
        Assert.Empty(s.ByKey);
        Assert.NotNull(s.LastError);
    }
}
