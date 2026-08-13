namespace UartTerminal.Core.Logging;

/// <summary>
/// 연속 로깅 파일의 <b>기본 이름</b> 생성 규칙 — <c>세션_포트_YYMMDD_HHMMSS.log</c>.
///
/// 여러 포트를 동시에 열고 로깅하는 것이 정상 사용이므로(포트마다 로거가 따로 돈다),
/// 기본 이름만 봐도 <b>어느 보드의 어느 포트를 언제 받았는지</b> 알 수 있어야 파일이 섞이지 않는다.
/// 세션 없이 포트만 골라 연 경우에는 앞의 세션 칸을 <b>구분자까지 함께</b> 뺀다
/// (빈 칸을 남기면 <c>_COM4_…</c> 처럼 앞에 밑줄이 뜬다).
/// </summary>
public static class LogFileName
{
    public const string Extension = ".log";

    /// <summary>세션 이름이 파일명으로 그대로 쓰이므로 길이를 제한한다(경로 상한 여유).</summary>
    public const int MaxSessionPart = 40;

    /// <summary>
    /// 예: 세션 "sensor" · COM4 · 2026-08-13 12:03:35 → <c>sensor_COM4_260813_120335.log</c>,
    /// 세션이 없으면 <c>COM4_260813_120335.log</c>.
    /// </summary>
    public static string Default(string? sessionName, string portName, DateTime when)
    {
        string a = Sanitize(sessionName, MaxSessionPart);
        string b = Sanitize(portName, 32);
        string c = when.ToString("yyMMdd");
        string d = when.ToString("HHmmss");

        string stem = a.Length > 0 ? $"{a}_{b}_{c}_{d}" : $"{b}_{c}_{d}";
        // 포트명까지 비는 것은 이론상뿐이지만, 그때 ".log" 만 남으면 숨김 파일이 된다.
        if (stem.Length == 0 || stem.StartsWith('_')) stem = "UartTerminal_" + c + "_" + d;
        return stem + Extension;
    }

    /// <summary>
    /// 파일명에 못 쓰는 문자는 빼고, 공백 묶음은 <c>-</c> 로 바꾼다 — 세션 이름은 사람이 자유롭게
    /// 붙이는 값이라("pulse simul") 그대로 쓰면 이름에 공백이 섞이거나 저장 자체가 실패한다.
    /// </summary>
    private static string Sanitize(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        bool pendingGap = false;
        foreach (char ch in s.Trim())
        {
            if (char.IsWhiteSpace(ch)) { pendingGap = sb.Length > 0; continue; }
            if (Array.IndexOf(invalid, ch) >= 0) continue;
            if (pendingGap) { sb.Append('-'); pendingGap = false; }
            sb.Append(ch);
            if (sb.Length >= max) break;
        }
        // 끝의 '.'/'-' 는 윈도우에서 잘리거나 보기 나쁘다.
        return sb.ToString().TrimEnd('.', '-');
    }
}
