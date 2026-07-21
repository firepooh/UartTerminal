using System.Text;

namespace UartTerminal.Core.Terminal;

/// <summary>
/// MCP <c>uart_read</c>/<c>uart_expect</c>의 <c>strip_ansi</c>용 텍스트 정리 유틸리티.
/// AnsiParser 와 동일한 이스케이프 구조를 인식하되 <b>버리기만</b> 한다(화면 상태를 만들지 않음).
/// AI 소비용이므로 인쇄 문자 + 탭 + 개행만 남기고, CSI/OSC/기타 ESC 시퀀스와 제어 문자는 제거하며
/// CRLF/단독 CR 을 LF 로 정규화한다.
/// </summary>
public static class AnsiText
{
    private const char ESC = '';
    private const char BEL = '';

    private enum S { Ground, Esc, EscInter, Csi, Osc, OscEsc }

    /// <summary>문자열에서 ANSI 이스케이프/제어를 제거해 평문으로 반환.</summary>
    public static string Strip(string s) => Strip(s.AsSpan());

    public static string Strip(ReadOnlySpan<char> chars)
    {
        var sb = new StringBuilder(chars.Length);
        var st = S.Ground;

        for (int i = 0; i < chars.Length; i++)
        {
            char ch = chars[i];
            switch (st)
            {
                case S.Ground:
                    switch (ch)
                    {
                        case ESC: st = S.Esc; break;
                        case '\r':
                            // CRLF → LF, 단독 CR 은 제거(라인 편집 오버라이트는 평문화에서 생략)
                            if (i + 1 < chars.Length && chars[i + 1] == '\n') { /* LF 에서 처리 */ }
                            break;
                        case '\n': sb.Append('\n'); break;
                        case '\t': sb.Append('\t'); break;
                        default:
                            // 인쇄 문자만 유지(C0 제어 0x00~0x1F 및 DEL 0x7F 제거). 한글 등 >=0x20 은 통과.
                            if (ch >= 0x20 && ch != '') sb.Append(ch);
                            break;
                    }
                    break;

                case S.Esc:
                    st = ch switch
                    {
                        '[' => S.Csi,
                        ']' => S.Osc,
                        '(' or ')' or '*' or '+' => S.EscInter,
                        _ => S.Ground // ESC 7/8/=/>/c/D/E/M 등: 소비
                    };
                    break;

                case S.EscInter:
                    st = S.Ground; // 문자셋 지정 문자 한 글자 소비
                    break;

                case S.Csi:
                    // 파라미터/중간 바이트를 소비하다 최종 바이트(0x40~0x7E)에서 종료
                    if (ch >= 0x40 && ch <= 0x7E) st = S.Ground;
                    break;

                case S.Osc:
                    if (ch == BEL) st = S.Ground;      // OSC 종료(BEL)
                    else if (ch == ESC) st = S.OscEsc; // ST(ESC \) 가능성
                    break;

                case S.OscEsc:
                    st = S.Ground; // ESC \ 또는 그 외 → 종료
                    break;
            }
        }
        return sb.ToString();
    }
}
