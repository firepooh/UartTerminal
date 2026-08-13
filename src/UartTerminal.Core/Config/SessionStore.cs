using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Core.Config;

/// <summary>
/// 이름 붙인 접속 프로필. 저장하는 것은 <b>이름·포트·속도·열 때 리셋·개행 + (선택) 명령 그룹</b>이다.
/// 8N1/흐름제어는 고정값이며(README §2), 오픈 시 DTR/RTS deassert 도 고정이다 —
/// 리셋은 '펄스를 줄지 말지'라는 의도이므로 <see cref="ResetOnOpen"/> 한 항목으로만 노출한다(§2.2).
/// </summary>
public sealed record SessionProfile
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("port")] public string Port { get; init; } = "";
    [JsonPropertyName("baud")] public int Baud { get; init; } = 115200;

    /// <summary>
    /// 이 세션으로 접속할 때 EN 펄스로 보드를 리셋할지(ESP32 devkit 자동 리셋 회로). 기본 false.
    /// 보드마다 다르기 때문에 세션에 함께 저장한다(예: 부팅 로그를 늘 봐야 하는 보드 ↔ 리셋되면 안 되는 보드).
    /// </summary>
    [JsonPropertyName("resetOnOpen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ResetOnOpen { get; init; }

    /// <summary>
    /// 이 세션의 수신 개행 규약. <b>null = 지정 없음</b> → 접속할 때 현재(마지막으로 쓴) 값을 그대로 쓴다.
    /// 장치마다 개행이 다르므로(ESP-IDF=CR+LF, 구형 계측기=CR) 세션에 함께 저장한다. 알 수 없는 값도 null 로 취급.
    /// </summary>
    [JsonPropertyName("newlineRx")]
    [JsonConverter(typeof(TolerantNullableEnumConverter<ReceiveNewline>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReceiveNewline? NewlineRx { get; init; }

    /// <summary>이 세션의 송신 개행 규약. null = 지정 없음(현재 값 유지).</summary>
    [JsonPropertyName("newlineTx")]
    [JsonConverter(typeof(TolerantNullableEnumConverter<TransmitNewline>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransmitNewline? NewlineTx { get; init; }

    /// <summary>
    /// 이 세션으로 접속할 때 자동 선택할 명령 그룹 이름(commands.json 의 그룹). 비면 자동 선택하지 않는다.
    /// 프로젝트마다 명령 세트가 다른 경우를 위한 연결 고리(예: "proj A" 세션 ↔ "proj A" 명령 그룹).
    /// </summary>
    [JsonPropertyName("commandGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandGroup { get; init; }

    /// <summary>
    /// 이 세션으로 접속할 때 MCP 서버(이름 있는 파이프)를 자동으로 켤지. 기본 false.
    /// AI 도구를 늘 붙여 쓰는 보드와 그렇지 않은 보드가 갈리므로 세션에 함께 저장한다 —
    /// 켜는 것은 <b>탭 단위</b>라(포트마다 파이프가 다르다) 전역 설정으로는 표현할 수 없다.
    /// </summary>
    [JsonPropertyName("mcpOnOpen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool McpOnOpen { get; init; }

    /// <summary>
    /// 이 세션의 로그 저장 <b>폴더</b>(파일명이 아니다 — 이름은 열 때마다 규칙으로 새로 만든다).
    /// 보드/프로젝트마다 로그를 모으는 자리가 다르고, 전역 '마지막 파일' 하나만 기억하면
    /// 포트를 두 개 열었을 때 두 탭이 같은 파일을 제안받는다. 비면 마지막에 쓴 폴더를 쓴다.
    /// </summary>
    [JsonPropertyName("logFolder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogFolder { get; init; }

    /// <summary>목록 표시용: "모터보드 — COM24 · 115200 · ⟳ · MCP". 파생 값이므로 파일에 저장하지 않는다.</summary>
    [JsonIgnore]
    // 언어 중립 표기 — Core 는 문장을 만들지 않는다. ⟳ = 열 때 보드 리셋, MCP = 열 때 MCP 서버 켜기.
    public string Display =>
        $"{Name} — {Port} · {Baud}" + (ResetOnOpen ? " · ⟳" : "") + (McpOnOpen ? " · MCP" : "");

    /// <summary>UI 자동화/스크린리더가 읽는 이름(레코드 기본 ToString 은 타입/필드 덤프라 부적합).</summary>
    public override string ToString() => Display;
}

/// <summary>sessions.json 파일 형식(사람이 읽고 diff 할 수 있게 들여쓰기 저장).</summary>
public sealed class SessionFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = SessionStore.SupportedSchemaVersion;
    [JsonPropertyName("sessions")] public List<SessionProfile> Sessions { get; set; } = new();
}

/// <summary>
/// 접속 프로필 목록의 영속화(<c>sessions.json</c>). 손실 방어 방침은 <see cref="CommandStore"/>와 동일하다:
/// 원자 저장(.tmp→Replace) + <c>.bak</c> 보존, 파싱 실패는 <c>.corrupt-*</c> 로 보관, 읽기 실패 시 저장 잠금,
/// 상위 <c>schemaVersion</c> 저장 거부, 저장 성공 시에만 목록 커밋(유령 항목 방지), 실패는 <see cref="LastError"/>로 표면화.
/// 모든 접근은 UI 스레드 전제(변이는 편집 확정 시 1회)라 락을 두지 않는다.
/// </summary>
public sealed class SessionStore
{
    public const int SupportedSchemaVersion = 1;
    public const int MaxSessions = 50;
    public const int MaxNameLength = 40;

    /// <summary>로그 폴더 경로 상한(윈도우 확장 경로까지 고려한 넉넉한 값).</summary>
    public const int MaxPathLength = 500;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 한글 이름이 \uXXXX 로 저장되면 파일을 사람이 읽는 목적이 사라진다.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private List<SessionProfile> _items = new();

    public SessionStore(string path) => _path = path;

    public string FilePath => _path;
    public IReadOnlyList<SessionProfile> Items => _items;

    /// <summary>저장이 금지된 상태(상위 스키마 버전 또는 읽기 실패). 기존 파일을 덮어쓰지 않기 위한 잠금.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>마지막 Load/저장에서 사용자에게 알려야 하는 사유. 성공 시 null.</summary>
    public LocMessage? LastError { get; private set; }

    public event Action? Changed;

    public void Load()
    {
        LastError = null;
        IsReadOnly = false;

        if (!File.Exists(_path))
        {
            _items = new List<SessionProfile>();
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
            // 내용을 모르는 파일을 덮어쓰지 않도록 저장을 잠근다.
            _items = new List<SessionProfile>();
            IsReadOnly = true;
            LastError = LocMessage.Of("Sess.Err.CannotOpen", ex.GetType().Name, _path);
            RaiseChanged();
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<SessionFile>(json)
                       ?? throw new JsonException("empty document");
            if (file.SchemaVersion > SupportedSchemaVersion)
            {
                IsReadOnly = true;
                LastError = LocMessage.Of("Sess.Err.NewerSchema", file.SchemaVersion);
            }
            // "sessions": null 이면 System.Text.Json 이 초기화값을 null 로 덮어쓴다 → 빈 목록으로 취급.
            _items = Sanitize(file.Sessions ?? new List<SessionProfile>());
        }
        catch (JsonException ex)
        {
            _items = new List<SessionProfile>();
            LastError = LocMessage.Of("Sess.Err.Corrupt", ex.Message);
            PreserveCorrupt();
        }
        catch (Exception ex)
        {
            _items = new List<SessionProfile>();
            IsReadOnly = true;
            LastError = LocMessage.Of("Sess.Err.Unreadable", ex.GetType().Name, _path);
        }

        RaiseChanged();
    }

    /// <summary>목록 전체를 교체하고 저장. <b>저장 성공 시에만</b> 커밋·방송한다(파일에 없는 항목 표시 방지).</summary>
    public bool ReplaceAll(IEnumerable<SessionProfile> items)
    {
        if (IsReadOnly)
        {
            LastError ??= LocMessage.Of("Sess.Err.ReadOnly");
            return false;
        }

        var next = Sanitize(items);
        if (!SaveList(next)) return false;

        _items = next;
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 프로필 추가(같은 이름이 있으면 교체 — "같은 이름으로 다시 저장"이 자연스럽게 갱신되도록).
    /// 상한 초과 시 조용히 버리지 않고 실패를 반환한다.
    /// </summary>
    public bool AddOrReplace(SessionProfile item)
    {
        var list = new List<SessionProfile>(_items);
        int at = list.FindIndex(s => string.Equals(s.Name, item.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            list[at] = item;
        }
        else
        {
            if (list.Count >= MaxSessions)
            {
                LastError = LocMessage.Of("Sess.Err.TooMany", MaxSessions);
                return false;
            }
            list.Add(item);
        }
        return ReplaceAll(list);
    }

    public bool Remove(SessionProfile item)
    {
        var list = new List<SessionProfile>(_items);
        if (!list.Remove(item)) return false;
        return ReplaceAll(list);
    }

    /// <summary>
    /// 명령 그룹 이름이 바뀌었을 때 세션의 <see cref="SessionProfile.CommandGroup"/> 참조를 따라 갱신한다.
    ///
    /// 이 전파가 없으면 그룹 이름을 바꾸는 순간 세션이 <b>없는 그룹</b>을 가리키게 되고, 접속 시
    /// 조용히 첫 그룹으로 떨어져 "탭마다 다른 명령 세트" 가 깨진다 — 실기에서 실제로 그 상태였고,
    /// 사용자에게는 "탭마다 다른 그룹을 못 쓴다" 로 보였다.
    /// </summary>
    /// <param name="renames">옛 이름 → 새 이름. 대소문자 무시 비교를 쓰는 사전을 넘길 것.</param>
    /// <param name="changed">참조가 바뀐 세션 수.</param>
    /// <returns>저장까지 성공하면 true. 바뀔 것이 없으면 changed=0 · true.</returns>
    public bool TryApplyGroupRenames(IReadOnlyDictionary<string, string> renames, out int changed)
    {
        changed = 0;
        if (renames.Count == 0) return true;

        var next = new List<SessionProfile>(_items.Count);
        foreach (var s in _items)
        {
            if (s.CommandGroup is { Length: > 0 } g
                && renames.TryGetValue(g, out string? to)
                && !string.Equals(g, to, StringComparison.Ordinal))
            {
                next.Add(s with { CommandGroup = to });
                changed++;
            }
            else
            {
                next.Add(s);
            }
        }

        if (changed == 0) return true;
        if (ReplaceAll(next)) return true;

        changed = 0;   // 저장 실패 — 메모리 목록도 커밋되지 않았다(사유는 LastError)
        return false;
    }

    private bool SaveList(List<SessionProfile> items)
    {
        if (IsReadOnly)
        {
            LastError ??= LocMessage.Of("Sess.Err.ReadOnly");
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new SessionFile { SchemaVersion = SupportedSchemaVersion, Sessions = items };
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak");
            else
                File.Move(tmp, _path);

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = LocMessage.Of("Sess.Err.SaveFailed", ex.Message);
            return false;
        }
    }

    /// <summary>포트가 없는 항목 제거, 이름 공백 시 포트로 대체, 속도 범위 검증, 명령 그룹 정규화, 길이/개수 상한.</summary>
    private static List<SessionProfile> Sanitize(IEnumerable<SessionProfile> src)
    {
        var list = new List<SessionProfile>();
        foreach (var s in src)
        {
            if (s is null) continue;

            string port = OneLine(s.Port).ToUpperInvariant();
            if (port.Length == 0) continue; // 포트 없는 프로필은 무의미
            if (port.Length > 32) port = port[..32];

            string name = OneLine(s.Name);
            if (name.Length == 0) name = port;
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];

            int baud = s.Baud is >= 300 and <= 4_000_000 ? s.Baud : 115200;

            // 연결된 명령 그룹(선택). 공백은 null 로 정규화해 "없음"과 구분하지 않는다.
            string group = OneLine(s.CommandGroup);
            if (group.Length > MaxNameLength) group = group[..MaxNameLength];

            string folder = OneLine(s.LogFolder);
            if (folder.Length > MaxPathLength) folder = "";   // 잘린 경로는 엉뚱한 폴더를 가리킨다

            list.Add(new SessionProfile
            {
                Name = name,
                Port = port,
                Baud = baud,
                ResetOnOpen = s.ResetOnOpen,
                // 정의되지 않은 enum 값(손편집/구버전)은 '지정 없음'으로 떨어뜨린다.
                NewlineRx = Enum.IsDefined(s.NewlineRx ?? (ReceiveNewline)(-1)) ? s.NewlineRx : null,
                NewlineTx = Enum.IsDefined(s.NewlineTx ?? (TransmitNewline)(-1)) ? s.NewlineTx : null,
                CommandGroup = group.Length > 0 ? group : null,
                McpOnOpen = s.McpOnOpen,
                // 존재 여부는 검사하지 않는다 — 이동식 드라이브·네트워크 경로가 잠깐 없다고
                // 설정이 지워지면 안 된다(없으면 로깅 시작 시 만들거나 그때 알린다).
                LogFolder = folder.Length > 0 ? folder : null,
            });
            if (list.Count >= MaxSessions) break;
        }
        return list;
    }

    private static string OneLine(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private void PreserveCorrupt()
    {
        try
        {
            string dest = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, dest, overwrite: true);
            // 손상 메시지에 "원본을 어디로 보관했는지"까지 담은 키로 교체(문장 이어붙이기 대신).
            LastError = LocMessage.Of("Sess.Err.CorruptPreserved",
                LastError?.Args.FirstOrDefault() ?? "", Path.GetFileName(dest));
        }
        catch { }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
