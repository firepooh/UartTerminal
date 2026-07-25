using System.Text.Json;
using UartTerminal.Core.Config;

namespace UartTerminal.Tests;

/// <summary>
/// <see cref="CommandStore"/> 회귀 테스트. 사용자 저작 파일이므로 손실 방어(백업/손상 보존/읽기 실패 잠금)와
/// "한 줄 명령" 불변식이 핵심 검증 대상이다.
/// </summary>
public sealed class CommandStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public CommandStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uartterm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "commands.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private CommandStore NewStore() => new(_path);

    private static SavedCommand Cmd(string name, string text, bool confirm = false) =>
        new() { Name = name, Text = text, Confirm = confirm };

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
    public void ReplaceAll_RoundTripsThroughFile()
    {
        var a = NewStore();
        a.Load();
        Assert.True(a.ReplaceAll(new[] { Cmd("heap", "free"), Cmd("리셋", "restart", confirm: true) }));

        var b = NewStore();
        b.Load();
        Assert.Equal(2, b.Items.Count);
        Assert.Equal("heap", b.Items[0].Name);
        Assert.Equal("free", b.Items[0].Text);
        Assert.False(b.Items[0].Confirm);
        Assert.Equal("리셋", b.Items[1].Name);
        Assert.Equal("restart", b.Items[1].Text);
        Assert.True(b.Items[1].Confirm);
        Assert.Null(b.LastError);
    }

    [Fact]
    public void Save_PreservesPreviousVersionAsBak()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[] { Cmd("v1", "first") });
        Assert.False(File.Exists(_path + ".bak")); // 첫 저장에는 직전 버전이 없음

        s.ReplaceAll(new[] { Cmd("v2", "second") });
        Assert.True(File.Exists(_path + ".bak"));
        Assert.Contains("first", File.ReadAllText(_path + ".bak"));
        Assert.Contains("second", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp")); // 임시 파일은 남지 않음
    }

    [Fact]
    public void Add_AppendsToEnd()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[] { Cmd("a", "aa") });
        Assert.True(s.Add(Cmd("b", "bb")));
        Assert.Equal(new[] { "aa", "bb" }, s.Items.Select(i => i.Text));
    }

    [Fact]
    public void CorruptFile_IsPreservedAndStoreStartsEmpty()
    {
        File.WriteAllText(_path, "{ this is not json ");
        var s = NewStore();
        s.Load();

        Assert.Empty(s.Items);
        Assert.NotNull(s.LastError);
        Assert.False(s.IsReadOnly); // 손상은 보관 후 새로 저장 가능
        var preserved = Directory.GetFiles(_dir, "commands.json.corrupt-*");
        Assert.Single(preserved);
        Assert.Contains("not json", File.ReadAllText(preserved[0]));
        Assert.False(File.Exists(_path)); // 원본은 보관 위치로 이동됨

        // 손상 이후에도 정상 저장이 가능해야 함
        Assert.True(s.ReplaceAll(new[] { Cmd("ok", "free") }));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void HigherSchemaVersion_IsReadOnly_AndSaveRefused()
    {
        // 신버전이 만든 파일: 모르는 필드가 있어도 소실시키지 않도록 저장을 거부해야 한다
        File.WriteAllText(_path,
            """
            {
              "schemaVersion": 99,
              "commands": [ { "name": "future", "text": "cmd", "unknownField": 1 } ]
            }
            """);

        var s = NewStore();
        s.Load();

        Assert.True(s.IsReadOnly);
        Assert.NotNull(s.LastError);
        Assert.Single(s.Items); // 최선 노력으로 읽기는 함
        Assert.Equal("future", s.Items[0].Name);

        string before = File.ReadAllText(_path);
        Assert.False(s.ReplaceAll(new[] { Cmd("x", "y") })); // 저장 거부
        Assert.Equal(before, File.ReadAllText(_path));       // 파일 불변
        Assert.NotNull(s.LastError);
    }

    [Fact]
    public void UnreadableFile_LocksSaving_SoDataIsNotDestroyed()
    {
        File.WriteAllText(_path, """{ "schemaVersion": 1, "commands": [ { "name": "keep", "text": "free" } ] }""");

        // 파일을 배타적으로 열어 읽기 실패를 유도
        using (var hold = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var s = NewStore();
            s.Load();

            Assert.True(s.IsReadOnly);
            Assert.NotNull(s.LastError);
            Assert.Empty(s.Items);
            Assert.False(s.ReplaceAll(new[] { Cmd("x", "y") })); // 내용을 모르는 파일을 덮어쓰지 않음
        }

        // 잠금 해제 후 원본이 그대로 남아 있어야 한다
        var reload = NewStore();
        reload.Load();
        Assert.Single(reload.Items);
        Assert.Equal("keep", reload.Items[0].Name);
    }

    [Fact]
    public void Sanitize_EnforcesSingleLineInvariant()
    {
        var s = NewStore();
        s.Load();
        // 손편집으로 다중 라인(=시퀀스)을 넣어도 한 줄로 접힌다
        s.ReplaceAll(new[] { Cmd("multi", "first\r\nsecond") });
        Assert.Equal("first  second", s.Items[0].Text);
        Assert.DoesNotContain('\n', s.Items[0].Text);
        Assert.DoesNotContain('\r', s.Items[0].Text);
    }

    [Fact]
    public void Sanitize_DropsEmptyText_AndDefaultsNameToText()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[] { Cmd("이름만", "   "), Cmd("", "log_level * debug") });

        Assert.Single(s.Items);
        Assert.Equal("log_level * debug", s.Items[0].Text);
        Assert.Equal("log_level * debug", s.Items[0].Name); // 이름 비면 텍스트로 대체
    }

    [Fact]
    public void Sanitize_TruncatesOverlongAndCapsCount()
    {
        var s = NewStore();
        s.Load();

        var many = Enumerable.Range(0, CommandStore.MaxCommands + 25)
            .Select(i => Cmd($"n{i}", $"cmd{i}"))
            .Append(Cmd(new string('N', CommandStore.MaxNameLength + 10), new string('T', CommandStore.MaxTextLength + 10)))
            .ToArray();
        s.ReplaceAll(many);

        Assert.Equal(CommandStore.MaxCommands, s.Items.Count);
        Assert.All(s.Items, c => Assert.True(c.Name.Length <= CommandStore.MaxNameLength));
        Assert.All(s.Items, c => Assert.True(c.Text.Length <= CommandStore.MaxTextLength));
    }

    [Fact]
    public void Changed_FiresOnLoadAndReplace()
    {
        var s = NewStore();
        int n = 0;
        s.Changed += () => n++;

        s.Load();
        Assert.Equal(1, n);

        s.ReplaceAll(new[] { Cmd("a", "aa") });
        Assert.Equal(2, n);
    }

    [Fact]
    public void SavedFile_IsHumanReadableJson()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[] { Cmd("heap", "free") });

        string json = File.ReadAllText(_path);
        Assert.Contains("\n", json);                 // 들여쓰기(diff 가능)
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"name\": \"heap\"", json);
        Assert.DoesNotContain("\"confirm\"", json);   // false 는 파일에 쓰지 않음(잡음 감소)

        using var doc = JsonDocument.Parse(json);     // 유효한 JSON
        Assert.Equal(1, doc.RootElement.GetProperty("commands").GetArrayLength());
    }

    [Fact]
    public void SavedFile_KeepsKoreanLiteral_NotUnicodeEscaped()
    {
        // 파일을 사람이 읽고 diff/공유하는 것이 목적이므로 한글이 \uXXXX 로 이스케이프되면 안 된다.
        var s = NewStore();
        s.Load();
        s.ReplaceAll(new[] { Cmd("리셋", "restart", confirm: true) });

        string json = File.ReadAllText(_path);
        Assert.Contains("\"name\": \"리셋\"", json);
        Assert.DoesNotContain("\\u", json);

        var b = NewStore();  // 그래도 왕복은 정상
        b.Load();
        Assert.Equal("리셋", b.Items[0].Name);
    }
}
