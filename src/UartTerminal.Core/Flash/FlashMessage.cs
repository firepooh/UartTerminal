namespace UartTerminal.Core.Flash;

/// <summary>
/// 사용자에게 보여줄 메시지를 <b>번역 키 + 인자</b>로 표현한다.
///
/// Core 는 UI(언어 설정)를 모르는 순수 로직이라 완성된 문장을 만들면 계층이 섞인다.
/// 그래서 Core 는 "무엇을 알려야 하는지"만 정하고, 문장은 UI 가 현재 언어로 조립한다.
/// 부수 효과로 테스트가 한국어 문장 대신 키를 검사하게 되어, 문구를 다듬어도 테스트가 깨지지 않는다.
///
/// 인자는 <b>이미 표시 형태로 만든 문자열</b>이다(예: 오프셋은 "0x10000").
/// 16진 표기나 파일명 같은 형식은 값을 아는 Core 가 정하는 것이 맞다.
/// </summary>
public sealed record FlashMessage
{
    public required string Key { get; init; }

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public static FlashMessage Of(string key, params object?[] args) => new()
    {
        Key = key,
        Args = args.Select(a => a?.ToString() ?? "").ToArray(),
    };

    /// <summary>번역 없이 볼 때(로그·테스트 실패 메시지)용 표현.</summary>
    public override string ToString() =>
        Args.Count == 0 ? Key : $"{Key}({string.Join(", ", Args)})";
}
