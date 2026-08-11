using UartTerminal.Core.Config;

namespace UartTerminal.Tests;

/// <summary>
/// 세션이 <b>지금은 없는 그룹 이름</b>을 가리킬 때의 계약.
///
/// 실기에서 이런 상태가 실제로 나왔다: sessions.json 의 두 세션이 각각 다른 그룹을 지정해 뒀는데
/// 그 이름의 그룹이 commands.json 에 없어서(지우거나 이름을 바꾼 뒤 세션은 옛 이름을 그대로 보관)
/// 두 탭이 <b>모두 첫 그룹</b>을 보여줬고, 사용자는 "탭마다 다른 그룹을 못 쓴다"로 겪었다.
/// 폴백 자체는 필요하지만(없는 그룹을 표시할 수는 없다) <b>조용히</b> 넘기면 안 된다.
/// </summary>
public sealed class CommandGroupFallbackTests : IDisposable
{
    private readonly string _dir;
    private readonly CommandStore _store;

    public CommandGroupFallbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uart-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new CommandStore(Path.Combine(_dir, "commands.json"));
        _store.Load();
        _store.ReplaceAllGroups(new[]
        {
            new CommandGroup { Name = "projA", Commands = { new SavedCommand { Name = "a", Text = "a" } } },
            new CommandGroup { Name = "projB", Commands = { new SavedCommand { Name = "b", Text = "b" } } },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>존재하는 그룹은 그대로 찾는다(대소문자 무시).</summary>
    [Theory]
    [InlineData("projA")]
    [InlineData("PROJA")]
    [InlineData("projB")]
    public void FindGroup_ResolvesExistingNames(string name)
    {
        Assert.NotNull(_store.FindGroup(name));
    }

    /// <summary>
    /// 없는 이름은 <c>null</c> 이어야 한다 — 여기서 첫 그룹을 돌려주면 호출자가
    /// "세션이 가리키는 그룹이 사라졌다"는 사실을 알 방법이 없어진다(UI 가 이 null 로 안내한다).
    /// </summary>
    [Theory]
    [InlineData("기본")]      // 예전 기본 그룹 이름
    [InlineData("projC")]     // 지워진 그룹
    [InlineData("proj A")]    // 공백이 다른 유사 이름
    public void FindGroup_ReturnsNull_ForMissingNames(string missing)
    {
        Assert.Null(_store.FindGroup(missing));
    }

    /// <summary>
    /// <see cref="CommandStore.CommandsOf"/> 는 없는 그룹에서 첫 그룹으로 폴백한다(표시용 편의).
    /// 이 동작 자체는 유지한다 — 다만 이것만 보고는 폴백이 일어났는지 알 수 없다는 점이
    /// 조용한 혼동의 원인이었으므로, 판별은 항상 <see cref="CommandStore.FindGroup"/> 로 해야 한다.
    /// </summary>
    [Fact]
    public void CommandsOf_FallsBackToFirstGroup_ButFindGroupStillReportsMissing()
    {
        var cmds = _store.CommandsOf("없는그룹");

        Assert.Equal(_store.Groups[0].Commands.Count, cmds.Count);   // 폴백은 그대로
        Assert.Null(_store.FindGroup("없는그룹"));                    // 판별은 구분 가능
    }

    /// <summary>세션에 저장된 그룹 이름은 그룹 목록과 독립이므로 어긋날 수 있다 — 그 상태를 고정해 둔다.</summary>
    [Fact]
    public void SessionCanReferenceAGroupThatNoLongerExists()
    {
        string path = Path.Combine(_dir, "sessions.json");
        var sessions = new SessionStore(path);
        sessions.Load();
        Assert.True(sessions.AddOrReplace(new SessionProfile
        {
            Name = "boardA", Port = "COM7", Baud = 115200, CommandGroup = "projC",
        }));

        var reloaded = new SessionStore(path);
        reloaded.Load();

        Assert.Equal("projC", reloaded.Items[0].CommandGroup);
        Assert.Null(_store.FindGroup(reloaded.Items[0].CommandGroup));  // 어긋난 상태가 저장된다
    }
}
