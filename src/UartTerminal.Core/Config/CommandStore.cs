using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UartTerminal.Core.Config;

/// <summary>
/// 저장된 <b>한 줄</b> 명령. 사용자가 입력바에 타이핑한 것과 바이트 단위로 동일하게 전송된다
/// (전송 시 CR 부착 — 코드베이스 전역의 단일 개행 규약).
/// 다단계 시퀀스·대기·조건 분기는 이 기능의 범위가 아니며 MCP(uart_send/uart_expect)가 담당한다.
/// </summary>
public sealed record SavedCommand
{
    /// <summary>버튼에 표시할 짧은 이름. 비어 있으면 <see cref="Text"/>를 그대로 쓴다.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>실제 전송할 문자열(개행 없음).</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";

    /// <summary>전송 전 확인을 받을지(restart/erase 류 위험 명령의 오클릭 방어).</summary>
    [JsonPropertyName("confirm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Confirm { get; init; }

    /// <summary>
    /// 하위 명령(폴더). 비어 있지 않으면 이 항목은 <b>폴더</b>로 취급되어 클릭 시 하위 목록에서 고르게 한다
    /// (예: "reset" → "sw reset" / "hw reset" / "wdt reset"). 폴더 자신의 <see cref="Text"/>는 전송하지 않는다.
    /// 중첩은 1단계까지만 허용한다(손편집으로 더 깊어지면 평탄화).
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedCommand>? Items { get; init; }

    /// <summary>하위 명령을 가진 폴더인지.</summary>
    [JsonIgnore]
    public bool IsFolder => Items is { Count: > 0 };
}

/// <summary>명령 그룹(프로젝트 단위). 세션에 연결해 접속 시 자동 선택할 수 있다.</summary>
public sealed record CommandGroup
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("commands")] public List<SavedCommand> Commands { get; init; } = new();

    public override string ToString() => Name;
}

/// <summary>commands.json 파일 형식(사람이 읽고 diff 할 수 있게 들여쓰기 저장).</summary>
public sealed class CommandFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CommandStore.SupportedSchemaVersion;

    /// <summary>v2: 그룹 목록. 프로젝트마다 다른 명령 세트를 담는다.</summary>
    [JsonPropertyName("groups")] public List<CommandGroup>? Groups { get; set; }

    /// <summary>v1 호환: 그룹 없는 평면 목록. 읽을 때 기본 그룹으로 승격된다(저장은 항상 v2).</summary>
    [JsonPropertyName("commands")] public List<SavedCommand>? Commands { get; set; }
}

/// <summary>
/// 저장 명령 목록의 영속화(<c>commands.json</c>). 창 좌표 같은 휘발성 UI 상태(state.json)와 달리
/// <b>사용자가 저작한 콘텐츠</b>이므로 손실 방어를 강화했다:
/// <list type="bullet">
///   <item>저장은 <c>.tmp</c> → <c>File.Replace</c> 원자 교체 + 직전 버전 <c>.bak</c> 보존</item>
///   <item>파싱 실패(손상)는 원본을 <c>.corrupt-*</c> 로 보관한 뒤 빈 목록으로 시작</item>
///   <item>읽기 자체가 실패(파일 잠김 등)하면 <see cref="IsReadOnly"/>로 잠가 <b>덮어쓰지 않는다</b></item>
///   <item>상위 <c>schemaVersion</c> 파일은 읽기 전용(신버전 필드 소실 방지)</item>
///   <item>실패는 <see cref="LastError"/>로 표면화한다(조용히 삼키지 않음)</item>
/// </list>
/// 모든 접근은 UI 스레드에서 이뤄지는 것을 전제로 하며(변이는 편집 확정 시 1회), 락을 두지 않는다.
/// </summary>
public sealed class CommandStore
{
    public const int SupportedSchemaVersion = 2;
    public const int MaxCommands = 200;      // 그룹 하나당 최상위 항목 수
    public const int MaxSubCommands = 50;    // 폴더 하나당 하위 항목 수
    public const int MaxGroups = 50;
    public const int MaxNameLength = 40;
    public const int MaxTextLength = 2000;

    /// <summary>그룹이 하나도 없는 파일을 승격할 때 쓰는 기본 그룹 이름.</summary>
    public const string DefaultGroupName = "기본";   // loc:data — commands.json 에 저장되는 그룹 이름

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 한글 등 비ASCII 를 \uXXXX 로 이스케이프하지 않는다 — 이 파일은 사람이 읽고 diff/공유하는 대상이므로
        // "리셋" 이 "리셋" 로 저장되면 목적을 잃는다. HTML 로 렌더링되지 않는 로컬 설정 파일이라 안전하다.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private List<CommandGroup> _groups = new();

    public CommandStore(string path) => _path = path;

    public string FilePath => _path;

    /// <summary>명령 그룹 목록(프로젝트 단위).</summary>
    public IReadOnlyList<CommandGroup> Groups => _groups;

    /// <summary>그룹 이름으로 명령 목록을 얻는다. 없으면 첫 그룹, 그것도 없으면 빈 목록.</summary>
    public IReadOnlyList<SavedCommand> CommandsOf(string? groupName)
    {
        var g = FindGroup(groupName) ?? (_groups.Count > 0 ? _groups[0] : null);
        return g?.Commands ?? (IReadOnlyList<SavedCommand>)Array.Empty<SavedCommand>();
    }

    /// <summary>이름으로 그룹 찾기(대소문자 무시). 없으면 null.</summary>
    public CommandGroup? FindGroup(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>그룹 이름 목록(UI 드롭다운용).</summary>
    public IReadOnlyList<string> GroupNames => _groups.Select(g => g.Name).ToList();

    /// <summary>
    /// 첫 그룹의 명령 목록(그룹을 쓰지 않는 단순 경로용 편의 API).
    /// 폴더 항목도 그대로 포함되므로 표시 측은 <see cref="SavedCommand.IsFolder"/>를 확인해야 한다.
    /// </summary>
    public IReadOnlyList<SavedCommand> Items =>
        _groups.Count > 0 ? _groups[0].Commands : (IReadOnlyList<SavedCommand>)Array.Empty<SavedCommand>();

    /// <summary>첫(또는 유일) 그룹의 명령을 통째로 교체(그룹을 쓰지 않는 단순 경로용 편의 API).</summary>
    public bool ReplaceAll(IEnumerable<SavedCommand> items)
    {
        var next = new List<CommandGroup>();
        if (_groups.Count == 0)
        {
            next.Add(new CommandGroup { Name = DefaultGroupName, Commands = items.ToList() });
        }
        else
        {
            next.Add(new CommandGroup { Name = _groups[0].Name, Commands = items.ToList() });
            for (int i = 1; i < _groups.Count; i++) next.Add(_groups[i]);
        }
        return ReplaceAllGroups(next);
    }

    /// <summary>저장이 금지된 상태(상위 스키마 버전 파일 또는 읽기 실패). 기존 파일을 덮어쓰지 않기 위한 잠금.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>마지막 Load/저장에서 사용자에게 알려야 하는 사유. 성공 시 null.</summary>
    public LocMessage? LastError { get; private set; }

    /// <summary>목록이 바뀔 때(로드/편집 확정) 발생. 열린 모든 창의 칩 바가 이를 구독해 갱신한다.</summary>
    public event Action? Changed;

    /// <summary>파일에서 목록을 읽는다. 파일이 없으면 빈 목록(정상).</summary>
    public void Load()
    {
        LastError = null;
        IsReadOnly = false;

        if (!File.Exists(_path))
        {
            _groups = new List<CommandGroup>();
            RaiseChanged();
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            // 읽기 실패(잠김/권한). 파일 내용을 모르는 상태이므로 저장을 잠가 사용자 데이터를 보호한다.
            _groups = new List<CommandGroup>();
            IsReadOnly = true;
            LastError = LocMessage.Of("Cmd.Err.CannotOpen", ex.GetType().Name, _path);
            RaiseChanged();
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<CommandFile>(json)
                       ?? throw new JsonException("empty document");
            if (file.SchemaVersion > SupportedSchemaVersion)
            {
                IsReadOnly = true;
                LastError = LocMessage.Of("Cmd.Err.NewerSchema", file.SchemaVersion);
            }

            if (file.Groups is { Count: > 0 })
            {
                _groups = SanitizeGroups(file.Groups);
            }
            else if (file.Commands is { Count: > 0 })
            {
                // v1 자동 마이그레이션: 평면 목록을 기본 그룹으로 승격(다음 저장에서 v2 로 기록).
                _groups = SanitizeGroups(new[]
                {
                    new CommandGroup { Name = DefaultGroupName, Commands = file.Commands }
                });
            }
            else
            {
                _groups = new List<CommandGroup>();
            }
        }
        catch (JsonException ex)
        {
            _groups = new List<CommandGroup>();
            LastError = LocMessage.Of("Cmd.Err.Corrupt", ex.Message);
            PreserveCorrupt();
        }
        catch (Exception ex)
        {
            // 예상 밖의 역직렬화 실패. 내용을 신뢰할 수 없으므로 저장을 잠가 원본을 보호하고,
            // 예외를 밖으로 내보내지 않는다(App 시작 경로에서 던지면 창 하나 없이 앱이 죽는다).
            _groups = new List<CommandGroup>();
            IsReadOnly = true;
            LastError = LocMessage.Of("Cmd.Err.Unreadable", ex.GetType().Name, _path);
        }

        RaiseChanged();
    }

    /// <summary>
    /// 목록 전체를 교체하고 저장(편집 확정 경로). <b>저장에 성공했을 때만</b> in-memory 목록을 커밋하고
    /// <see cref="Changed"/>를 발생시킨다 — 그렇지 않으면 칩 바가 디스크에 없는 '유령 명령'을 보여주게 된다.
    /// </summary>
    public bool ReplaceAllGroups(IEnumerable<CommandGroup> groups)
    {
        if (IsReadOnly)
        {
            LastError ??= LocMessage.Of("Cmd.Err.ReadOnly");
            return false;
        }

        var next = SanitizeGroups(groups);
        if (!SaveGroups(next)) return false;

        _groups = next;
        RaiseChanged();
        return true;
    }

    /// <summary>명령 하나를 지정 그룹 끝에 추가하고 저장("현재 입력 저장" 경로). 그룹이 없으면 만든다.</summary>
    public bool Add(SavedCommand item, string? groupName = null)
    {
        var target = FindGroup(groupName) ?? (_groups.Count > 0 ? _groups[0] : null);

        // 상한을 먼저 검사한다. Sanitize 는 초과분을 조용히 잘라내므로, 여기서 막지 않으면
        // 새 명령이 버려졌는데도 "저장됨"으로 보고된다.
        if (target is not null && target.Commands.Count >= MaxCommands)
        {
            LastError = LocMessage.Of("Cmd.Err.GroupFull", target.Name, MaxCommands);
            return false;
        }
        if (target is null && _groups.Count >= MaxGroups)
        {
            LastError = LocMessage.Of("Cmd.Err.TooManyGroups", MaxGroups);
            return false;
        }

        var next = new List<CommandGroup>();
        if (target is null)
        {
            next.AddRange(_groups);
            next.Add(new CommandGroup
            {
                Name = string.IsNullOrWhiteSpace(groupName) ? DefaultGroupName : groupName!,
                Commands = new List<SavedCommand> { item },
            });
        }
        else
        {
            foreach (var g in _groups)
            {
                if (ReferenceEquals(g, target))
                {
                    var list = new List<SavedCommand>(g.Commands) { item };
                    next.Add(new CommandGroup { Name = g.Name, Commands = list });
                }
                else next.Add(g);
            }
        }
        return ReplaceAllGroups(next);
    }

    private bool SaveGroups(List<CommandGroup> groups)
    {
        if (IsReadOnly)
        {
            LastError ??= LocMessage.Of("Cmd.Err.ReadOnly");
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new CommandFile
            {
                SchemaVersion = SupportedSchemaVersion,
                Groups = groups,
            };

            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak"); // 직전 버전 1개 보존(스키마 버그/오편집 복구용)
            else
                File.Move(tmp, _path);

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = LocMessage.Of("Cmd.Err.SaveFailed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 목록 정규화. "버튼 = 한 줄 문자열 전송" 불변식을 데이터 계층에서 강제한다:
    /// 개행은 공백으로 치환(손편집으로 다중 명령 시퀀스가 섞이는 것 차단), 빈 텍스트 항목 제거,
    /// 이름 공백 시 텍스트로 대체, 길이/개수 상한 적용.
    /// </summary>
    private static List<SavedCommand> Sanitize(IEnumerable<SavedCommand> src, bool allowFolders = true)
    {
        var list = new List<SavedCommand>();
        foreach (var c in src)
        {
            if (c is null) continue;

            // 폴더: 하위 명령이 있으면 폴더로 취급(자신의 Text 는 전송하지 않으므로 없어도 된다).
            // 중첩은 1단계까지 — 하위의 하위는 평탄화(allowFolders:false)해 데이터 계층에서 깊이를 강제한다.
            if (allowFolders && c.Items is { Count: > 0 })
            {
                var subs = Sanitize(c.Items, allowFolders: false);
                if (subs.Count > MaxSubCommands) subs = subs.GetRange(0, MaxSubCommands);
                if (subs.Count == 0) continue; // 유효 하위가 없으면 폴더도 버린다

                string fname = OneLine(c.Name);
                if (fname.Length == 0) fname = OneLine(c.Text);
                if (fname.Length == 0) fname = "(폴더)";   // loc:data — 파일에 저장되는 이름
                if (fname.Length > MaxNameLength) fname = fname[..MaxNameLength];

                list.Add(new SavedCommand { Name = fname, Text = "", Confirm = c.Confirm, Items = subs });
                if (list.Count >= MaxCommands) break;
                continue;
            }

            string text = OneLine(c.Text);
            if (text.Length == 0) continue;
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];

            string name = OneLine(c.Name);
            if (name.Length == 0) name = text;
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];

            list.Add(new SavedCommand { Name = name, Text = text, Confirm = c.Confirm });
            if (list.Count >= MaxCommands) break;
        }
        return list;
    }

    /// <summary>그룹 목록 정규화: 이름 정리(빈 이름 대체·중복 회피), 그룹 수 상한, 각 그룹의 명령 정규화.</summary>
    private static List<CommandGroup> SanitizeGroups(IEnumerable<CommandGroup> src)
    {
        var list = new List<CommandGroup>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in src)
        {
            if (g is null) continue;

            string name = OneLine(g.Name);
            if (name.Length == 0) name = DefaultGroupName;
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];
            // 이름은 세션 연결 키이므로 중복을 허용하지 않는다(뒤에 오는 동명 그룹에 접미사).
            if (!used.Add(name))
            {
                for (int i = 2; ; i++)
                {
                    string cand = $"{name} ({i})";
                    if (cand.Length > MaxNameLength) cand = cand[..MaxNameLength];
                    if (used.Add(cand)) { name = cand; break; }
                }
            }

            list.Add(new CommandGroup { Name = name, Commands = Sanitize(g.Commands ?? new List<SavedCommand>()) });
            if (list.Count >= MaxGroups) break;
        }
        return list;
    }

    private static string OneLine(string? s) =>
        (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>손상 파일을 타임스탬프 이름으로 보관(사용자가 직접 복구할 수 있게).</summary>
    private void PreserveCorrupt()
    {
        try
        {
            string dest = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, dest, overwrite: true);
            LastError = LocMessage.Of("Cmd.Err.CorruptPreserved",
                LastError?.Args.FirstOrDefault() ?? "", Path.GetFileName(dest));
        }
        catch
        {
            // 보관 실패 시 원본은 그대로 남는다(다음 저장에서 .bak 으로 밀려남).
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* 구독자 예외 격리 */ }
    }
}
