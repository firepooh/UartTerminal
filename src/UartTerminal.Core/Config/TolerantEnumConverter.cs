using System.Text.Json;
using System.Text.Json.Serialization;

namespace UartTerminal.Core.Config;

/// <summary>
/// 문자열 ↔ nullable enum 변환기. 기본 <see cref="JsonStringEnumConverter"/>와 달리
/// <b>알 수 없는 값이면 예외 대신 null</b>(= 지정 없음)로 처리한다.
///
/// 사용자가 손으로 고치는 파일(<c>sessions.json</c>)에서 오타 하나가 <c>JsonException</c> →
/// "파일 손상"(<c>.corrupt-*</c> 로 격리)으로 번지는 것을 막기 위한 것이다. 세션 하나의 옵션 값이
/// 이상하다고 저장된 프로필 전체를 잃을 이유는 없다.
/// </summary>
internal sealed class TolerantNullableEnumConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 문자열이 아니거나(숫자/객체 등) 이름이 맞지 않으면 '지정 없음'으로 흘린다.
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value))
            return value;
        return null;
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteStringValue(value.Value.ToString());
        else writer.WriteNullValue();
    }
}
