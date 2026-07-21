namespace UartTerminal.Core.Terminal;

/// <summary>
/// UAX #11 East Asian Width 기반의 셀 폭 계산.
/// 논리 라인 버퍼, soft-wrap, 커서 열 계산, 선택 영역이 모두 이 함수 하나만 참조한다.
/// (README §4.1) Ambiguous 폭 문자는 한국어 환경 기준 Narrow(1)로 처리한다.
/// </summary>
public static class CharWidth
{
    /// <summary>
    /// 인쇄 가능한 코드 포인트의 표시 폭(셀 수). 결합 문자/제로폭은 0, 전각은 2, 그 외 1.
    /// 제어 문자는 파서가 걸러내므로 이 함수에 전달되지 않는 것을 전제로 하되, 방어적으로 0을 반환.
    /// </summary>
    public static int Width(int codePoint)
    {
        // C0 / C1 제어 및 DEL: 인쇄 대상 아님
        if (codePoint < 0x20 || (codePoint >= 0x7F && codePoint < 0xA0))
            return 0;

        if (IsZeroWidth(codePoint))
            return 0;

        if (IsWide(codePoint))
            return 2;

        return 1;
    }

    private static bool IsZeroWidth(int cp)
    {
        // 결합 문자 및 제로폭 제어(부분 집합, 실무상 시리얼 로그에서 충분)
        return
            (cp >= 0x0300 && cp <= 0x036F) ||   // Combining Diacritical Marks
            (cp >= 0x0483 && cp <= 0x0489) ||
            (cp >= 0x0591 && cp <= 0x05BD) ||
            (cp >= 0x0610 && cp <= 0x061A) ||
            (cp >= 0x064B && cp <= 0x065F) ||
            (cp >= 0x1AB0 && cp <= 0x1AFF) ||   // Combining Diacritical Marks Extended
            (cp >= 0x1DC0 && cp <= 0x1DFF) ||   // Combining Diacritical Marks Supplement
            (cp >= 0x200B && cp <= 0x200F) ||   // ZWSP, ZWNJ, ZWJ, LRM/RLM
            (cp >= 0x20D0 && cp <= 0x20FF) ||   // Combining Marks for Symbols
            (cp >= 0xFE20 && cp <= 0xFE2F) ||   // Combining Half Marks
            cp == 0xFEFF;                       // ZWNBSP / BOM
    }

    private static bool IsWide(int cp)
    {
        return
            (cp >= 0x1100 && cp <= 0x115F) ||   // Hangul Jamo
            (cp >= 0x2E80 && cp <= 0x2EFF) ||   // CJK Radicals Supplement
            (cp >= 0x2F00 && cp <= 0x2FDF) ||   // Kangxi Radicals
            (cp >= 0x2FF0 && cp <= 0x2FFF) ||   // Ideographic Description Characters
            (cp >= 0x3000 && cp <= 0x303E) ||   // CJK Symbols and Punctuation (3000 = 전각 공백)
            (cp >= 0x3041 && cp <= 0x33FF) ||   // Hiragana, Katakana, CJK compat
            (cp >= 0x3400 && cp <= 0x4DBF) ||   // CJK Unified Ideographs Extension A
            (cp >= 0x4E00 && cp <= 0x9FFF) ||   // CJK Unified Ideographs
            (cp >= 0xA000 && cp <= 0xA4CF) ||   // Yi Syllables
            (cp >= 0xA960 && cp <= 0xA97F) ||   // Hangul Jamo Extended-A
            (cp >= 0xAC00 && cp <= 0xD7A3) ||   // Hangul Syllables (한글 음절)
            (cp >= 0xD7B0 && cp <= 0xD7FF) ||   // Hangul Jamo Extended-B
            (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK Compatibility Ideographs
            (cp >= 0xFE10 && cp <= 0xFE19) ||   // Vertical Forms
            (cp >= 0xFE30 && cp <= 0xFE4F) ||   // CJK Compatibility Forms
            (cp >= 0xFF00 && cp <= 0xFF60) ||   // Fullwidth Forms
            (cp >= 0xFFE0 && cp <= 0xFFE6) ||   // Fullwidth Signs
            (cp >= 0x1F300 && cp <= 0x1FAFF) || // Emoji / Symbols (대부분 Wide)
            (cp >= 0x20000 && cp <= 0x3FFFD);   // CJK Ext B~ (Supplementary Ideographic Plane)
    }
}
