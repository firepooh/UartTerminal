using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UartTerminal.Core.Config;

/// <summary>
/// 이름 붙인 접속 프로필. 저장하는 것은 <b>이름·포트·속도·열 때 리셋 + (선택) 명령 그룹</b>이다.
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
    /// 이 세션으로 접속할 때 자동 선택할 명령 그룹 이름(commands.json 의 그룹). 비면 자동 선택하지 않는다.
    /// 프로젝트마다 명령 세트가 다른 경우를 위한 연결 고리(예: "proj A" 세션 ↔ "proj A" 명령 그룹).
    /// </summary>
    [JsonPropertyName("commandGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandGroup { get; init; }

    /// <summary>목록 표시용: "모터보드 — COM24 · 115200 · 리셋". 파생 값이므로 파일에 저장하지 않는다.</summary>
    [JsonIgnore]
    public string Display => ResetOnOpen ? $"{Name} — {Port} · {Baud} · 리셋" : $"{Name} — {Port} · {Baud}";

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
    public string? LastError { get; private set; }

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
            LastError = $"세션 파일을 열 수 없습니다({ex.GetType().Name}). 이번 실행에서는 저장하지 않습니다: {_path}";
            RaiseChanged();
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<SessionFile>(json)
                       ?? throw new JsonException("빈 문서");
            if (file.SchemaVersion > SupportedSchemaVersion)
            {
                IsReadOnly = true;
                LastError = $"세션 파일이 최신 버전(v{file.SchemaVersion})에서 만들어졌습니다. 읽기 전용으로 엽니다.";
            }
            // "sessions": null 이면 System.Text.Json 이 초기화값을 null 로 덮어쓴다 → 빈 목록으로 취급.
            _items = Sanitize(file.Sessions ?? new List<SessionProfile>());
        }
        catch (JsonException ex)
        {
            _items = new List<SessionProfile>();
            LastError = $"세션 파일이 손상되었습니다: {ex.Message}";
            PreserveCorrupt();
        }
        catch (Exception ex)
        {
            _items = new List<SessionProfile>();
            IsReadOnly = true;
            LastError = $"세션 파일을 해석할 수 없습니다({ex.GetType().Name}). 이번 실행에서는 저장하지 않습니다: {_path}";
        }

        RaiseChanged();
    }

    /// <summary>목록 전체를 교체하고 저장. <b>저장 성공 시에만</b> 커밋·방송한다(파일에 없는 항목 표시 방지).</summary>
    public bool ReplaceAll(IEnumerable<SessionProfile> items)
    {
        if (IsReadOnly)
        {
            LastError ??= "세션 파일이 읽기 전용 상태여서 저장하지 않았습니다.";
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
                LastError = $"저장된 세션이 최대 {MaxSessions}개입니다 — 목록에서 삭제 후 다시 저장하세요.";
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

    private bool SaveList(List<SessionProfile> items)
    {
        if (IsReadOnly)
        {
            LastError ??= "세션 파일이 읽기 전용 상태여서 저장하지 않았습니다.";
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
            LastError = $"세션 파일 저장 실패: {ex.Message}";
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

            list.Add(new SessionProfile
            {
                Name = name,
                Port = port,
                Baud = baud,
                ResetOnOpen = s.ResetOnOpen,
                CommandGroup = group.Length > 0 ? group : null,
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
            LastError += $" 원본을 {Path.GetFileName(dest)} 로 보관했습니다.";
        }
        catch { }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
