namespace UartTerminal.Core.Serial;

/// <summary>
/// 링버퍼에서 잘라낸 바이트 조각이 멀티바이트 UTF-8 문자 중간에서 끝날 때, 마지막의 불완전한
/// 후행 시퀀스를 잘라내는 헬퍼. <c>uart_read</c> 가 커서를 <b>완전한 문자 경계</b>까지만 전진시켜
/// 한글 등 멀티바이트가 읽기 경계에서 깨지지 않게 한다(남은 바이트는 다음 읽기에서 이어짐).
/// </summary>
public static class Utf8Boundary
{
    /// <summary>
    /// <paramref name="bytes"/> 중 마지막에 완결되지 않은 UTF-8 시퀀스를 제외한, 완전한 부분의 길이를 반환.
    /// 완전하면 <c>bytes.Length</c> 를 그대로 반환. (선행 바이트 기준으로만 판정 — 유효성 검사는 아님.)
    /// </summary>
    public static int CompleteLength(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length;
        if (n == 0) return 0;

        // 마지막 최대 3바이트를 되짚어 선행(lead) 바이트를 찾는다.
        for (int back = 1; back <= 3 && back <= n; back++)
        {
            byte b = bytes[n - back];
            if ((b & 0x80) == 0x00)
                return n;                 // ASCII: 그 자체로 완결 → 전체 완전
            if ((b & 0xC0) == 0x80)
                continue;                 // 후속(continuation) 바이트: 더 앞으로

            // 선행 바이트: 기대 시퀀스 길이 산출
            int expected =
                (b & 0xE0) == 0xC0 ? 2 :
                (b & 0xF0) == 0xE0 ? 3 :
                (b & 0xF8) == 0xF0 ? 4 : 1; // 1 = 잘못된 선행 → 그대로 둠

            int have = back; // 이 선행 바이트부터 끝까지 확보된 바이트 수
            // 완결됐으면 전체 길이, 아니면 이 선행 바이트 앞까지만 완전
            return have >= expected ? n : n - back;
        }

        // 3바이트를 되짚어도 선행을 못 찾음(비정상) → 전체 반환
        return n;
    }
}
