using System.Text.Json;
using System.Text.Json.Serialization;

namespace UartTerminal.Core.Parsing;

/// <summary>
/// 메시지 한 필드의 정의. 이름 외에는 전부 선택 —
/// 정의가 없는 항목은 원시 값 그대로 보여 준다(모르는 것을 아는 척하지 않는다).
/// </summary>
public sealed record FieldSpec
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>표시 단위(예: "V", "km/h"). 값 뒤에 붙는다.</summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>숫자 값에 곱할 배율(예: 100mV 단위 → 0.1 로 V 표시). null = 그대로.</summary>
    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale { get; init; }

    /// <summary>
    /// 값 해석 형식: <c>"datetime"</c>(yyyyMMddHHmmss → 사람이 읽는 시각),
    /// <c>"epoch"</c>(unix 초 → 로컬 시각). null = 해석 없음.
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    /// <summary>원시 값 → 사람이 읽는 라벨(예: "0" → "비활성"). 매칭 안 되면 원시 값 그대로.</summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Enum { get; init; }

    /// <summary>
    /// 비트필드 해석: 비트 번호(문자열) → 라벨. 켜진 비트의 라벨을 나열해 보여준다
    /// (예: 21963 → "모뎀, GNSS 유효, …"). <b>enum 이 먼저다</b> — 255=미지원 같은 센티널이
    /// 비트 해석("모든 비트 켜짐")으로 오독되지 않게.
    /// </summary>
    [JsonPropertyName("bits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Bits { get; init; }

    /// <summary>원시 값의 진법(비트 해석용). 기본 10 — "FF"·"55CB" 처럼 hex 로 오는 필드는 16.</summary>
    [JsonPropertyName("radix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Radix { get; init; }
}

/// <summary>키 하나(예: "T5")로 식별되는 메시지의 정의.</summary>
public sealed record MessageSpec
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";

    /// <summary>패널 머리글에 키 옆에 표시할 이름(예: "상태 보고").</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>필드 구분 문자. 기본 "_".</summary>
    [JsonPropertyName("separator")] public string Separator { get; init; } = "_";

    /// <summary>
    /// 패널에서 기본으로 접어 둘지(제목만 표시, 클릭으로 펼침). 매 보고에 따라붙는 헤더성
    /// 메시지처럼 '있다는 것만 알면 되는' 키에 쓴다. 어떤 키가 그런지는 정의 파일이 정한다 —
    /// 앱은 특정 키를 모른다.
    /// </summary>
    [JsonPropertyName("collapsed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Collapsed { get; init; }

    [JsonPropertyName("fields")] public List<FieldSpec> Fields { get; init; } = new();
}

/// <summary>parsers.json 파일 형식.</summary>
public sealed class ParserFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = ParserStore.SupportedSchemaVersion;
    [JsonPropertyName("messages")] public List<MessageSpec> Messages { get; set; } = new();
}

/// <summary>
/// 파서 정의(<c>parsers.json</c>) 로더. <b>읽기 전용</b>이다 — 이 파일은 사용자가(또는 변환 도구가)
/// 작성하며 앱은 절대 고쳐 쓰지 않는다. 프로토콜 필드 정의는 사용자 자산이라 앱 저장소에
/// 들어가지 않고(commands.json 과 같은 방침), 팀 공유는 파일 복사다.
/// </summary>
public sealed class ParserStore
{
    public const int SupportedSchemaVersion = 1;

    private string _path;
    private Dictionary<string, MessageSpec> _byKey = new(StringComparer.Ordinal);
    private List<MessageSpec> _items = new();

    public ParserStore(string path) => _path = path;

    public string FilePath => _path;

    /// <summary>정의 파일 경로 교체(프로젝트마다 다른 정의 파일을 골라 쓰는 흐름). 반영은 다음 Load.</summary>
    public void SetPath(string path) => _path = path;

    /// <summary>키("T5" 등) → 정의. 대소문자 구분(프로토콜 키는 정확해야 한다).</summary>
    public IReadOnlyDictionary<string, MessageSpec> ByKey => _byKey;

    /// <summary>파일에 적힌 순서 그대로의 정의 목록(필터 체크박스가 이 순서로 나열된다).</summary>
    public IReadOnlyList<MessageSpec> Items => _items;

    /// <summary>마지막 Load 에서 사용자에게 알릴 사유(손상 등). 성공/파일 없음은 null.</summary>
    public LocMessage? LastError { get; private set; }

    /// <summary>정의 파일이 존재했는지(없음 = 안내 문구용, 오류 아님).</summary>
    public bool FileExists { get; private set; }

    public void Load()
    {
        LastError = null;
        FileExists = File.Exists(_path);
        var next = new Dictionary<string, MessageSpec>(StringComparer.Ordinal);
        var order = new List<MessageSpec>();

        if (FileExists)
        {
            try
            {
                var file = JsonSerializer.Deserialize<ParserFile>(File.ReadAllText(_path))
                           ?? throw new JsonException("empty document");
                if (file.SchemaVersion > SupportedSchemaVersion)
                {
                    LastError = LocMessage.Of("Parse.Err.NewerSchema", file.SchemaVersion);
                }
                else
                {
                    foreach (var m in file.Messages ?? new List<MessageSpec>())
                    {
                        if (m is null || string.IsNullOrWhiteSpace(m.Key)) continue;
                        // 같은 키가 두 번 나오면 뒤가 이긴다(손편집에서 흔한 상태 — 조용히 무시하지 않고 덮되 단순하게).
                        var spec = m with
                        {
                            Key = m.Key.Trim(),
                            Separator = string.IsNullOrEmpty(m.Separator) ? "_" : m.Separator,
                            Fields = m.Fields ?? new List<FieldSpec>(),
                        };
                        if (next.ContainsKey(spec.Key))
                            order.RemoveAll(s => s.Key == spec.Key);
                        next[spec.Key] = spec;
                        order.Add(spec);
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = LocMessage.Of("Parse.Err.Corrupt", ex.Message);
            }
        }

        _byKey = next;
        _items = order;
    }
}
