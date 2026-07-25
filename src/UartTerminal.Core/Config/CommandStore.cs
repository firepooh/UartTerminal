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
}

/// <summary>commands.json 파일 형식(사람이 읽고 diff 할 수 있게 들여쓰기 저장).</summary>
public sealed class CommandFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CommandStore.SupportedSchemaVersion;
    [JsonPropertyName("commands")] public List<SavedCommand> Commands { get; set; } = new();
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
    public const int SupportedSchemaVersion = 1;
    public const int MaxCommands = 200;
    public const int MaxNameLength = 40;
    public const int MaxTextLength = 2000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 한글 등 비ASCII 를 \uXXXX 로 이스케이프하지 않는다 — 이 파일은 사람이 읽고 diff/공유하는 대상이므로
        // "리셋" 이 "리셋" 로 저장되면 목적을 잃는다. HTML 로 렌더링되지 않는 로컬 설정 파일이라 안전하다.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private List<SavedCommand> _items = new();

    public CommandStore(string path) => _path = path;

    public string FilePath => _path;

    public IReadOnlyList<SavedCommand> Items => _items;

    /// <summary>저장이 금지된 상태(상위 스키마 버전 파일 또는 읽기 실패). 기존 파일을 덮어쓰지 않기 위한 잠금.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>마지막 Load/저장에서 사용자에게 알려야 하는 사유. 성공 시 null.</summary>
    public string? LastError { get; private set; }

    /// <summary>목록이 바뀔 때(로드/편집 확정) 발생. 열린 모든 창의 칩 바가 이를 구독해 갱신한다.</summary>
    public event Action? Changed;

    /// <summary>파일에서 목록을 읽는다. 파일이 없으면 빈 목록(정상).</summary>
    public void Load()
    {
        LastError = null;
        IsReadOnly = false;

        if (!File.Exists(_path))
        {
            _items = new List<SavedCommand>();
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
            _items = new List<SavedCommand>();
            IsReadOnly = true;
            LastError = $"명령 파일을 열 수 없습니다({ex.GetType().Name}). 이번 실행에서는 저장하지 않습니다: {_path}";
            RaiseChanged();
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<CommandFile>(json)
                       ?? throw new JsonException("빈 문서");
            if (file.SchemaVersion > SupportedSchemaVersion)
            {
                IsReadOnly = true;
                LastError = $"명령 파일이 최신 버전(v{file.SchemaVersion})에서 만들어졌습니다. 읽기 전용으로 엽니다.";
            }
            _items = Sanitize(file.Commands);
        }
        catch (JsonException ex)
        {
            _items = new List<SavedCommand>();
            LastError = $"명령 파일이 손상되었습니다: {ex.Message}";
            PreserveCorrupt();
        }

        RaiseChanged();
    }

    /// <summary>목록 전체를 교체하고 저장(편집 확정 경로). 저장 성공 여부를 반환한다.</summary>
    public bool ReplaceAll(IEnumerable<SavedCommand> items)
    {
        _items = Sanitize(items);
        bool ok = Save();
        RaiseChanged();
        return ok;
    }

    /// <summary>명령 하나를 목록 끝에 추가하고 저장("현재 입력 저장" 경로).</summary>
    public bool Add(SavedCommand item)
    {
        var list = new List<SavedCommand>(_items) { item };
        return ReplaceAll(list);
    }

    private bool Save()
    {
        if (IsReadOnly)
        {
            LastError ??= "명령 파일이 읽기 전용 상태여서 저장하지 않았습니다.";
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new CommandFile
            {
                SchemaVersion = SupportedSchemaVersion,
                Commands = new List<SavedCommand>(_items),
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
            LastError = $"명령 파일 저장 실패: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 목록 정규화. "버튼 = 한 줄 문자열 전송" 불변식을 데이터 계층에서 강제한다:
    /// 개행은 공백으로 치환(손편집으로 다중 명령 시퀀스가 섞이는 것 차단), 빈 텍스트 항목 제거,
    /// 이름 공백 시 텍스트로 대체, 길이/개수 상한 적용.
    /// </summary>
    private static List<SavedCommand> Sanitize(IEnumerable<SavedCommand> src)
    {
        var list = new List<SavedCommand>();
        foreach (var c in src)
        {
            if (c is null) continue;

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

    private static string OneLine(string? s) =>
        (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>손상 파일을 타임스탬프 이름으로 보관(사용자가 직접 복구할 수 있게).</summary>
    private void PreserveCorrupt()
    {
        try
        {
            string dest = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, dest, overwrite: true);
            LastError += $" 원본을 {Path.GetFileName(dest)} 로 보관했습니다.";
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
