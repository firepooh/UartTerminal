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

    // ── 비트필드 ────────────────────────────────────────────────────────────

    private static MessageSpec B1 => new()
    {
        Key = "B1",
        Name = "bits",
        Fields = new List<FieldSpec>
        {
            new() { Name = "dec", Bits = new() { ["0"] = "modem", ["3"] = "gps", ["14"] = "kbox" } },
            new()
            {
                Name = "hex", Radix = 16,
                Bits = new() { ["2"] = "volt", ["6"] = "soh" },
                Enum = new() { ["FF"] = "not-supported" },   // enum 이 비트 해석보다 먼저
            },
        },
    };

    [Fact]
    public void Bits_ListSetBitLabels_InBitOrder()
    {
        // 9 = bit0 + bit3
        var b = MessageParser.ParseLine("B1=9_44", Specs(B1))[0];
        Assert.Equal("modem, gps", b.Fields[0].Decoded);
        // hex "44" = 0x44 = bit2 + bit6
        Assert.Equal("volt, soh", b.Fields[1].Decoded);
    }

    /// <summary>정의에 없는 비트가 켜지면 b{n} 으로 드러낸다 — 조용히 삼키면 정의가 낡은 게 안 보인다.</summary>
    [Fact]
    public void Bits_UnnamedSetBits_AppearAsBn()
    {
        // 11 = bit0 + bit1(이름 없음) + bit3
        var b = MessageParser.ParseLine("B1=11_0", Specs(B1))[0];
        Assert.Equal("modem, b1, gps", b.Fields[0].Decoded);
    }

    /// <summary>0·파싱 불가·이름 있는 비트가 하나도 안 켜진 값은 해석하지 않는다(원시 값만).</summary>
    [Fact]
    public void Bits_ZeroOrUnknown_KeepRawOnly()
    {
        Assert.Null(MessageParser.ParseLine("B1=0_0", Specs(B1))[0].Fields[0].Decoded);
        Assert.Null(MessageParser.ParseLine("B1=abc_0", Specs(B1))[0].Fields[0].Decoded);
        // 2 = bit1 뿐 — 이름 있는 비트가 없다
        Assert.Null(MessageParser.ParseLine("B1=2_0", Specs(B1))[0].Fields[0].Decoded);
    }

    /// <summary>255=미지원 같은 센티널은 비트 해석("전부 켜짐")으로 오독되면 안 된다 — enum 이 먼저.</summary>
    [Fact]
    public void Bits_EnumSentinel_TakesPrecedence()
    {
        var b = MessageParser.ParseLine("B1=1_FF", Specs(B1))[0];
        Assert.Equal("not-supported", b.Fields[1].Decoded);
    }

    // ── match / afterLine (콘솔 명령 응답 — "KEY=본문" 꼴이 아닌 출력) ──────

    private static MessageSpec R1 => new()
    {
        Key = "R1",
        Name = "regex-status",
        Match = @"-status:\s*([0-9A-Fa-f]{8})",
        Fields = new List<FieldSpec>
        {
            new() { Name = "flags", Radix = 16, Bits = new() { ["0"] = "alpha", ["4"] = "beta" } },
        },
    };

    private static MessageSpec N1 => new()
    {
        Key = "N1",
        Name = "next-line",
        Separator = " ",
        AfterLine = "current sensor info",
        Fields = new List<FieldSpec>
        {
            new() { Name = "id" },
            new() { Name = "flags", Radix = 16 },
        },
    };

    /// <summary>match 정의: 정규식 그룹 1 이 본문 — "-status: 00000011" 류의 명령 응답 라인.</summary>
    [Fact]
    public void Match_CapturesGroupAsBody()
    {
        var blocks = MessageParser.ParseLine("-status: 00000011", Specs(R1));
        Assert.Single(blocks);
        Assert.Equal("00000011", blocks[0].Fields[0].Raw);
        Assert.Equal("alpha, beta", blocks[0].Fields[0].Decoded);   // 0x11 = bit0 + bit4

        Assert.Empty(MessageParser.ParseLine("-other: 00000011", Specs(R1)));
        Assert.True(MessageParser.ContainsAnyKey("-status: 00000011", Specs(R1)));
    }

    /// <summary>afterLine 정의: 직전 줄이 헤더와 매치될 때만 현재 줄 전체가 본문이다.</summary>
    [Fact]
    public void AfterLine_BodyComesFromNextLine()
    {
        var blocks = MessageParser.ParseLine("7 3F", Specs(N1), prevLine: "current sensor info : ");
        Assert.Single(blocks);
        Assert.Equal("7", blocks[0].Fields[0].Raw);
        Assert.Equal("3F", blocks[0].Fields[1].Raw);

        // 직전 줄이 헤더가 아니면 같은 내용이라도 블록이 아니다 — 맨숫자 줄 오검출 방지.
        Assert.Empty(MessageParser.ParseLine("7 3F", Specs(N1), prevLine: "boot done"));
        Assert.Empty(MessageParser.ParseLine("7 3F", Specs(N1)));
    }

    /// <summary>여러 줄 텍스트(터미널 선택)는 줄 단위로 훑는다 — afterLine 이 줄 경계를 본다.</summary>
    [Fact]
    public void ParseText_HandlesMultiLineSelection()
    {
        var blocks = MessageParser.ParseText("current sensor info : \r\n7 3F\r\nS1=dev7_20260814023452_120_1", Specs(N1, S1));
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, b => b.Key == "N1");
        Assert.Contains(blocks, b => b.Key == "S1");
    }

    // ── subfields (마스크 비트 묶음 — 설정 워드 해석) ───────────────────────

    private static MessageSpec W1 => new()
    {
        Key = "W1",
        Name = "config-word",
        Fields = new List<FieldSpec>
        {
            new()
            {
                Name = "word", Radix = 16,
                Subfields = new List<SubfieldSpec>
                {
                    // 3비트 묶음(mask 0xE0, shift 5) + enum: 0 도 표시(기본값도 정보다)
                    new() { Name = "fuel", Mask = "0xE0", Enum = new() { ["0"] = "gasoline", ["4"] = "ev" } },
                    // 단일 비트 + enum 없음: 켜졌을 때 이름만(플래그)
                    new() { Name = "turbo", Mask = "0x10" },
                    // 여러 비트 + enum 없음: 0 이 아닐 때 "이름=값"
                    new() { Name = "level", Mask = "0x03" },
                },
            },
        },
    };

    [Fact]
    public void Subfields_MaskShiftAndEnum()
    {
        // 0x92 = fuel(0xE0→4=ev) + turbo(0x10) + level(0x03→2)
        var b = MessageParser.ParseLine("W1=92", Specs(W1))[0];
        Assert.Equal("fuel=ev, turbo, level=2", b.Fields[0].Decoded);

        // 0x00: enum 있는 fuel 만 표시(기본값), 플래그·무명 묶음은 생략
        var z = MessageParser.ParseLine("W1=0", Specs(W1))[0];
        Assert.Equal("fuel=gasoline", z.Fields[0].Decoded);
    }

    /// <summary>"0x" 접두 값은 진법 설정과 무관하게 hex 로 읽는다 — 펌웨어가 %d·%X·0x%x 를 섞어 찍는다.</summary>
    [Fact]
    public void HexPrefix_IsAcceptedRegardlessOfRadix()
    {
        var b = MessageParser.ParseLine("W1=0x92", Specs(W1))[0];
        Assert.Equal("fuel=ev, turbo, level=2", b.Fields[0].Decoded);
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

    /// <summary>잘못된 match 정규식은 Load 에서 걸린다 — 수신 스캔 도중 처음 터지면 원인을 못 찾는다.</summary>
    [Fact]
    public void Load_InvalidRegex_ReportsError()
    {
        File.WriteAllText(_path, """
            { "schemaVersion": 1, "messages": [
                { "key": "R1", "match": "([unclosed", "fields": [] }
            ] }
            """);
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
