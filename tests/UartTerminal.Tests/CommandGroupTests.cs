using System.Text.Json;
using UartTerminal.Core.Config;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

/// <summary>
/// 명령 <b>그룹</b>(프로젝트별 세트)과 <b>폴더</b>(상위→하위 명령 선택) 기능의 회귀 테스트.
/// v1(평면 목록) 파일의 자동 마이그레이션과 1단계 중첩 강제도 함께 고정한다.
/// </summary>
public sealed class CommandGroupTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public CommandGroupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uartterm-grp-" + Guid.NewGuid().ToString("N"));
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

    private static SavedCommand Folder(string name, params SavedCommand[] subs) =>
        new() { Name = name, Text = "", Items = subs.ToList() };

    // ── 그룹 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Groups_SaveAndReload_RoundTrip()
    {
        var s = NewStore();
        s.Load();
        Assert.True(s.ReplaceAllGroups(new[]
        {
            new CommandGroup { Name = "proj A", Commands = new() { Cmd("heap", "free") } },
            new CommandGroup { Name = "proj B", Commands = new() { Cmd("ver", "version") } },
        }));

        var s2 = NewStore();
        s2.Load();
        Assert.Equal(new[] { "proj A", "proj B" }, s2.GroupNames);
        Assert.Equal("free", s2.CommandsOf("proj A")[0].Text);
        Assert.Equal("version", s2.CommandsOf("proj B")[0].Text);
    }

    [Fact]
    public void CommandsOf_UnknownGroup_FallsBackToFirst()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[] { new CommandGroup { Name = "A", Commands = new() { Cmd("x", "x") } } });
        Assert.Equal("x", s.CommandsOf("없는그룹")[0].Text);
    }

    [Fact]
    public void DuplicateGroupNames_AreDisambiguated()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[]
        {
            new CommandGroup { Name = "dup", Commands = new() { Cmd("a", "a") } },
            new CommandGroup { Name = "dup", Commands = new() { Cmd("b", "b") } },
        });
        // 이름은 세션 연결 키라 중복을 허용하지 않는다.
        Assert.Equal(2, s.Groups.Count);
        Assert.NotEqual(s.Groups[0].Name, s.Groups[1].Name);
    }

    [Fact]
    public void Add_TargetsNamedGroup()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[]
        {
            new CommandGroup { Name = "A", Commands = new() { Cmd("a1", "a1") } },
            new CommandGroup { Name = "B", Commands = new() { Cmd("b1", "b1") } },
        });

        Assert.True(s.Add(Cmd("b2", "b2"), "B"));
        Assert.Single(s.CommandsOf("A"));
        Assert.Equal(2, s.CommandsOf("B").Count);
    }

    // ── v1 → v2 마이그레이션 ─────────────────────────────────────────────────────

    [Fact]
    public void V1FlatFile_MigratesIntoDefaultGroup()
    {
        File.WriteAllText(_path, """
        {
          "schemaVersion": 1,
          "commands": [ { "name": "heap", "text": "free" } ]
        }
        """);

        var s = NewStore();
        s.Load();
        Assert.Null(s.LastError);
        Assert.False(s.IsReadOnly);
        Assert.Single(s.Groups);
        Assert.Equal(CommandStore.DefaultGroupName, s.Groups[0].Name);
        Assert.Equal("free", s.Groups[0].Commands[0].Text);
    }

    [Fact]
    public void V1File_ReSavedAsV2Groups()
    {
        File.WriteAllText(_path, """
        { "schemaVersion": 1, "commands": [ { "name": "a", "text": "a" } ] }
        """);
        var s = NewStore();
        s.Load();
        Assert.True(s.Add(Cmd("b", "b")));

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("groups", out _));
    }

    // ── 폴더(하위 명령) ──────────────────────────────────────────────────────────

    [Fact]
    public void Folder_RoundTrips_WithSubCommands()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[]
        {
            new CommandGroup
            {
                Name = "A",
                Commands = new()
                {
                    Folder("reset", Cmd("sw", "reset sw"), Cmd("hw", "reset hw"), Cmd("wdt", "reset wdt")),
                    Cmd("heap", "free"),
                },
            },
        });

        var s2 = NewStore();
        s2.Load();
        var items = s2.CommandsOf("A");
        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsFolder);
        Assert.Equal("reset", items[0].Name);
        Assert.Equal(3, items[0].Items!.Count);
        Assert.Equal("reset hw", items[0].Items![1].Text);
        Assert.False(items[1].IsFolder);
    }

    [Fact]
    public void Folder_WithNoValidSubs_IsDropped()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[]
        {
            new CommandGroup { Name = "A", Commands = new() { Folder("empty"), Cmd("ok", "ok") } },
        });
        var items = s.CommandsOf("A");
        Assert.Single(items);
        Assert.Equal("ok", items[0].Name);
    }

    [Fact]
    public void NestedFolders_AreFlattenedToOneLevel()
    {
        // 손편집으로 2단계 이상 중첩된 파일 — 하위의 하위는 평탄화되어야 한다.
        var deep = new SavedCommand
        {
            Name = "outer",
            Items = new()
            {
                new SavedCommand { Name = "inner", Text = "", Items = new() { Cmd("leaf", "leaf cmd") } },
                Cmd("direct", "direct cmd"),
            },
        };

        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[] { new CommandGroup { Name = "A", Commands = new() { deep } } });

        var folder = s.CommandsOf("A")[0];
        Assert.True(folder.IsFolder);
        // "inner" 는 전송 문자열이 없고 하위도 허용되지 않으므로 버려지고, "direct" 만 남는다.
        Assert.All(folder.Items!, sub => Assert.False(sub.IsFolder));
        Assert.Contains(folder.Items!, sub => sub.Text == "direct cmd");
    }

    [Fact]
    public void Folder_SubCommand_KeepsConfirmFlag()
    {
        var s = NewStore();
        s.Load();
        s.ReplaceAllGroups(new[]
        {
            new CommandGroup { Name = "A", Commands = new() { Folder("danger", Cmd("erase", "erase all", confirm: true)) } },
        });
        var s2 = NewStore();
        s2.Load();
        Assert.True(s2.CommandsOf("A")[0].Items![0].Confirm);
    }

    // ── 세션 연결 ────────────────────────────────────────────────────────────────

    [Fact]
    public void SessionProfile_PersistsCommandGroup()
    {
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        Assert.True(store.AddOrReplace(new SessionProfile
        {
            Name = "보드A", Port = "COM4", Baud = 115200, CommandGroup = "proj A",
        }));

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.Equal("proj A", reload.Items[0].CommandGroup);
    }

    [Fact]
    public void SessionProfile_WithoutGroup_IsNull()
    {
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        store.AddOrReplace(new SessionProfile { Name = "b", Port = "COM5", Baud = 9600 });

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.Null(reload.Items[0].CommandGroup);
    }

    // ── 열 때 보드 리셋(세션별 접속 속성) ────────────────────────────────────────

    [Fact]
    public void SessionProfile_PersistsResetOnOpen()
    {
        // Sanitize 가 새 필드를 빠뜨리면(과거 CommandGroup 에서 실제로 겪은 버그) 여기서 잡힌다.
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        Assert.True(store.AddOrReplace(new SessionProfile
        {
            Name = "부트 진단", Port = "COM4", Baud = 74880, ResetOnOpen = true,
        }));

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.True(reload.Items[0].ResetOnOpen);
    }

    [Fact]
    public void SessionProfile_ResetOnOpen_DefaultsFalse_AndIsOmittedFromFile()
    {
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        store.AddOrReplace(new SessionProfile { Name = "평범", Port = "COM5", Baud = 115200 });

        // 기본값(false)은 파일에 쓰지 않는다 — 손으로 읽는 파일을 조용히 유지.
        Assert.DoesNotContain("resetOnOpen", File.ReadAllText(sp));

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.False(reload.Items[0].ResetOnOpen);
    }

    [Fact]
    public void SessionProfile_Display_ShowsResetOnly_WhenOn()
    {
        var on = new SessionProfile { Name = "A", Port = "COM4", Baud = 115200, ResetOnOpen = true };
        var off = new SessionProfile { Name = "B", Port = "COM4", Baud = 115200 };
        Assert.Contains("리셋", on.Display);
        Assert.DoesNotContain("리셋", off.Display);
    }

    // ── 개행 규약(세션별 접속 속성 · null = 지정 없음) ──────────────────────────

    [Fact]
    public void SessionProfile_PersistsNewline()
    {
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        Assert.True(store.AddOrReplace(new SessionProfile
        {
            Name = "계측기", Port = "COM9", Baud = 9600,
            NewlineRx = ReceiveNewline.Cr, NewlineTx = TransmitNewline.CrLf,
        }));

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.Equal(ReceiveNewline.Cr, reload.Items[0].NewlineRx);
        Assert.Equal(TransmitNewline.CrLf, reload.Items[0].NewlineTx);
    }

    [Fact]
    public void SessionProfile_Newline_SavedAsReadableNames()
    {
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        store.AddOrReplace(new SessionProfile
        {
            Name = "a", Port = "COM4", Baud = 115200,
            NewlineRx = ReceiveNewline.Auto, NewlineTx = TransmitNewline.Lf,
        });

        // 사람이 읽고 고치는 파일이므로 숫자(0/1/2)가 아니라 이름으로 저장돼야 한다.
        string json = File.ReadAllText(sp);
        Assert.Contains("\"newlineRx\": \"Auto\"", json);
        Assert.Contains("\"newlineTx\": \"Lf\"", json);
    }

    [Fact]
    public void SessionProfile_NoNewline_IsOmitted_AndReadsBackNull()
    {
        // '세션 설정 없이 열 때' 시나리오: 세션이 개행을 지정하지 않으면 필드 자체가 없고,
        // 읽으면 null → 호출자는 현재(마지막으로 쓴) 값을 그대로 쓴다.
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        store.AddOrReplace(new SessionProfile { Name = "기본", Port = "COM4", Baud = 115200 });

        string json = File.ReadAllText(sp);
        Assert.DoesNotContain("newlineRx", json);
        Assert.DoesNotContain("newlineTx", json);

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.Null(reload.Items[0].NewlineRx);
        Assert.Null(reload.Items[0].NewlineTx);
    }

    [Fact]
    public void SessionProfile_UnknownNewlineName_FallsBackToNull_WithoutCorruptingFile()
    {
        // 손편집 오타가 파일 전체를 '손상'으로 만들어 프로필을 날리면 안 된다(관용 변환기).
        File.WriteAllText(Path.Combine(_dir, "sessions.json"), """
        {
          "schemaVersion": 1,
          "sessions": [
            { "name": "오타", "port": "COM4", "baud": 115200, "newlineRx": "CRLF!", "newlineTx": 3 }
          ]
        }
        """);

        var store = new SessionStore(Path.Combine(_dir, "sessions.json"));
        store.Load();

        Assert.Null(store.LastError);            // 손상 처리되지 않음
        Assert.Single(store.Items);              // 프로필은 살아 있음
        Assert.Equal("오타", store.Items[0].Name);
        Assert.Null(store.Items[0].NewlineRx);   // 알 수 없는 값 → 지정 없음
        Assert.Null(store.Items[0].NewlineTx);
    }

    [Fact]
    public void SessionProfile_PerSession_ValuesAreIndependent()
    {
        // 프로젝트마다 다른 값을 갖는 것이 이 기능의 핵심 — 전역 설정이면 이 테스트가 무의미해진다.
        string sp = Path.Combine(_dir, "sessions.json");
        var store = new SessionStore(sp);
        store.Load();
        Assert.True(store.ReplaceAll(new[]
        {
            new SessionProfile { Name = "리셋보드", Port = "COM4", Baud = 115200, ResetOnOpen = true },
            new SessionProfile { Name = "유지보드", Port = "COM7", Baud = 921600, ResetOnOpen = false },
        }));

        var reload = new SessionStore(sp);
        reload.Load();
        Assert.True(reload.Items[0].ResetOnOpen);
        Assert.False(reload.Items[1].ResetOnOpen);
    }
}
