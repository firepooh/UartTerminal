using UartTerminal.Core.Config;

namespace UartTerminal.Tests;

/// <summary>
/// <see cref="SessionStore"/> 회귀 테스트. CommandStore 리뷰에서 확정된 결함들(null 형태로 인한 크래시,
/// 저장 실패 시 유령 항목, 상한 초과 조용한 폐기)이 이 스토어에서도 재발하지 않는지 함께 검증한다.
/// </summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uartterm-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "sessions.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SessionStore NewStore() => new(_path);

    private static SessionProfile P(string name, string port, int baud = 115200) =>
        new() { Name = name, Port = port, Baud = baud };

    [Fact]
    public void Load_NoFile_EmptyAndNoError()
    {
        var s = NewStore();
        s.Load();
        Assert.Empty(s.Items);
        Assert.Null(s.LastError);
        Assert.False(s.IsReadOnly);
    }

    [Fact]
    public void RoundTrip_PreservesNamePortBaud()
    {
        var a = NewStore();
        a.Load();
        Assert.True(a.ReplaceAll(new[] { P("모터보드", "COM24", 921600), P("센서", "COM7") }));

        var b = NewStore();
        b.Load();
        Assert.Equal(2, b.Items.Count);
        Assert.Equal("모터보드", b.Items[0].Name);
        Assert.Equal("COM24", b.Items[0].Port);
        Assert.Equal(921600, b.Items[0].Baud);
        Assert.Equal(115200, b.Items[1].Baud);

        // 한글이 그대로 저장되어야 한다(파일은 사람이 읽고 공유하는 대상)
        string json = File.ReadAllText(_path);
        Assert.Contains("\"name\": \"모터보드\"", json);
        Assert.DoesNotContain("\\u", json);
        // 파생 값(Display)은 파일에 쓰지 않는다 — 손편집해도 무시되는 필드를 남기면 혼란만 준다
        Assert.DoesNotContain("Display", json);
    }

    [Fact]
    public void AddOrReplace_SameName_UpdatesInsteadOfDuplicating()
    {
        var s = NewStore();
        s.Load();
        s.AddOrReplace(P("보드", "COM3", 115200));
        s.AddOrReplace(P("보드", "COM9", 921600)); // 같은 이름 → 갱신

        Assert.Single(s.Items);
        Assert.Equal("COM9", s.Items[0].Port);
        Assert.Equal(921600, s.Items[0].Baud);
    }

    [Fact]
    public void AddOrReplace_DifferentNamesSamePort_BothKept()
    {
        // 같은 보드를 속도만 달리해 두 프로필로 두는 것은 정당한 사용 사례(부트 진단 74880 vs 로그 921600)
        var s = NewStore();
        s.Load();
        s.AddOrReplace(P("보드-부트", "COM24", 74880));
        s.AddOrReplace(P("보드-로그", "COM24", 921600));
        Assert.Equal(2, s.Items.Count);
    }

    [Fact]
    public void Remove_DeletesAndPersists()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[] { P("a", "COM1"), P("b", "COM2") });
        Assert.True(s.Remove(s.Items[0]));

        var b = NewStore();
        b.Load();
        Assert.Single(b.Items);
        Assert.Equal("b", b.Items[0].Name);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "sessions": null }""")]
    [InlineData("""{ "schemaVersion": 1, "sessions": [ null ] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("null")]
    public void NullShapes_DoNotThrow(string json)
    {
        // 앱 시작 경로가 이 Load 를 호출한다 — 예외가 나가면 창 하나 없이 앱이 죽는다.
        File.WriteAllText(_path, json);
        var s = NewStore();
        s.Load();
        Assert.Empty(s.Items);
    }

    [Fact]
    public void CorruptFile_IsPreservedAndStoreStartsEmpty()
    {
        File.WriteAllText(_path, "{ broken ");
        var s = NewStore();
        s.Load();

        Assert.Empty(s.Items);
        Assert.NotNull(s.LastError);
        Assert.Single(Directory.GetFiles(_dir, "sessions.json.corrupt-*"));
        Assert.True(s.ReplaceAll(new[] { P("ok", "COM1") })); // 손상 후에도 새로 저장 가능
    }

    [Fact]
    public void HigherSchemaVersion_IsReadOnly_AndLeavesListUnchanged()
    {
        File.WriteAllText(_path,
            """{ "schemaVersion": 99, "sessions": [ { "name": "future", "port": "COM5", "baud": 115200 } ] }""");
        var s = NewStore();
        s.Load();
        Assert.True(s.IsReadOnly);
        Assert.Single(s.Items);

        string before = File.ReadAllText(_path);
        int changed = 0;
        s.Changed += () => changed++;

        Assert.False(s.ReplaceAll(new[] { P("ghost", "COM9") }));
        Assert.Equal(before, File.ReadAllText(_path)); // 파일 불변
        Assert.Single(s.Items);                        // 목록 불변(유령 항목 없음)
        Assert.Equal("future", s.Items[0].Name);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void UnreadableFile_LocksSaving()
    {
        File.WriteAllText(_path, """{ "schemaVersion": 1, "sessions": [ { "name": "keep", "port": "COM1" } ] }""");
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var s = NewStore();
            s.Load();
            Assert.True(s.IsReadOnly);
            Assert.False(s.ReplaceAll(new[] { P("x", "COM2") }));
        }

        var reload = NewStore();
        reload.Load();
        Assert.Single(reload.Items);
        Assert.Equal("keep", reload.Items[0].Name);
    }

    [Fact]
    public void Sanitize_DropsPortlessAndDefaultsNameAndBaud()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[]
        {
            P("포트없음", "   "),            // 포트 없는 프로필 → 제거
            P("", "com7"),                  // 이름 없음 → 포트로 대체, 포트는 대문자 정규화
            P("범위밖", "COM8", 12),         // 비정상 속도 → 115200
        });

        Assert.Equal(2, s.Items.Count);
        Assert.Equal("COM7", s.Items[0].Port);
        Assert.Equal("COM7", s.Items[0].Name);
        Assert.Equal(115200, s.Items[1].Baud);
    }

    [Fact]
    public void AddOrReplace_AtCapacity_FailsLoudly()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(Enumerable.Range(0, SessionStore.MaxSessions).Select(i => P($"s{i}", $"COM{i + 1}")));
        Assert.Equal(SessionStore.MaxSessions, s.Items.Count);

        Assert.False(s.AddOrReplace(P("overflow", "COM99")));
        Assert.NotNull(s.LastError);
        Assert.DoesNotContain(s.Items, x => x.Name == "overflow");

        // 상한이어도 '기존 이름 갱신'은 허용되어야 한다
        Assert.True(s.AddOrReplace(P("s0", "COM77", 921600)));
        Assert.Equal("COM77", s.Items[0].Port);
    }

    [Fact]
    public void Display_IsHumanReadable()
    {
        Assert.Equal("모터보드 — COM24 · 921600", P("모터보드", "COM24", 921600).Display);
    }
}
