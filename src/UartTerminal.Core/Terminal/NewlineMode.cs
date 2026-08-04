namespace UartTerminal.Core.Terminal;

/// <summary>
/// 수신 개행 규약(TeraTerm 의 [Setup > Terminal] New-line Receive 에 대응).
/// 우리 화면 모델은 <b>논리 라인</b> 로그라서 LF 가 언제나 줄을 새로 만든다(셀 격자가 아니므로
/// LF 만 왔을 때 열이 유지되는 계단 현상이 없다). 그래서 각 모드는 "어느 바이트를 개행으로 볼지"의 선택이다.
/// </summary>
public enum ReceiveNewline
{
    /// <summary>기본. 개행=LF, CR=줄 처음으로 커서 이동(진행바 <c>\r</c> 덮어쓰기가 살아 있다). CRLF·LF 모두 한 줄.</summary>
    CrLf,

    /// <summary>개행=LF, CR=무시. <c>\r</c> 로 덮어쓰지 않고 받은 순서대로 남긴다.</summary>
    Lf,

    /// <summary>개행=CR, LF=무시. CR 만 개행으로 쓰는 장치(구형 계측기 등)용. CRLF 도 한 줄.</summary>
    Cr,

    /// <summary>
    /// 자동. CR·LF·CR+LF·LF+CR 어느 쪽이든 개행 1회로 본다(TeraTerm AUTO 규칙).
    /// 붙어 있는 CR/LF 쌍만 합치므로 <c>CR CR</c> 은 2줄이다. 대신 단독 <c>\r</c> 덮어쓰기는 개행이 된다.
    /// </summary>
    Auto,
}

/// <summary>송신 개행 규약(TeraTerm New-line Transmit). Enter 키·명령 칩·붙여넣기에 공통 적용.</summary>
public enum TransmitNewline
{
    /// <summary>기본. esp_console(linenoise)이 기대하는 CR(0x0D).</summary>
    Cr,

    /// <summary>CR+LF(0x0D 0x0A). 윈도우 계열 호스트/일부 AT 명령 장치.</summary>
    CrLf,

    /// <summary>LF(0x0A). 유닉스 계열 셸.</summary>
    Lf,
}

/// <summary>개행 모드의 표기/바이트 변환.</summary>
public static class NewlineModes
{
    /// <summary>송신 개행 바이트열.</summary>
    public static byte[] Bytes(this TransmitNewline mode) => mode switch
    {
        TransmitNewline.CrLf => new byte[] { 0x0D, 0x0A },
        TransmitNewline.Lf => new byte[] { 0x0A },
        _ => new byte[] { 0x0D },
    };

    /// <summary>송신 개행 문자열(붙여넣기 정규화용).</summary>
    public static string Text(this TransmitNewline mode) => mode switch
    {
        TransmitNewline.CrLf => "\r\n",
        TransmitNewline.Lf => "\n",
        _ => "\r",
    };

    public static string Label(this TransmitNewline mode) => mode switch
    {
        TransmitNewline.CrLf => "CR+LF",
        TransmitNewline.Lf => "LF",
        _ => "CR",
    };

    public static string Label(this ReceiveNewline mode) => mode switch
    {
        ReceiveNewline.Cr => "CR",
        ReceiveNewline.Lf => "LF",
        ReceiveNewline.Auto => "자동",
        _ => "CR+LF",
    };
}
