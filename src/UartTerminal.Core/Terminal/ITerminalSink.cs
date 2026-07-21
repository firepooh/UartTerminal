namespace UartTerminal.Core.Terminal;

/// <summary>
/// ANSI 파서가 호출하는 터미널 동작의 계약. 파서는 바이트/문자를 의미 동작으로 바꾸고,
/// 구현체(<see cref="TerminalBuffer"/>)는 논리 라인 버퍼에 반영한다.
/// 구현체의 스레드 동기화는 호출자(엔진)가 <see cref="TerminalBuffer.SyncRoot"/> 락을 잡는 것으로 보장한다.
/// </summary>
public interface ITerminalSink
{
    /// <summary>커서 위치에 문자 하나를 출력.</summary>
    void Print(char ch, CellAttributes attr);

    /// <summary>LF: 다음 줄로(현재 라인 확정 + 새 라인 시작).</summary>
    void LineFeed();

    /// <summary>CR: 커서를 줄 시작으로.</summary>
    void CarriageReturn();

    /// <summary>BS: 커서 한 칸 뒤로(비파괴).</summary>
    void Backspace();

    /// <summary>HT: 다음 8칸 탭 정지점까지 공백 출력.</summary>
    void HorizontalTab(CellAttributes attr);

    /// <summary>CUF(ESC[nC): 커서를 앞으로 n칸(필요 시 공백 패딩). 현재 라인 내에서만.</summary>
    void CursorForward(int n, CellAttributes attr);

    /// <summary>CUB(ESC[nD): 커서를 뒤로 n칸.</summary>
    void CursorBack(int n);

    /// <summary>CHA(ESC[nG): 커서를 라인 내 절대 열 n(1-기준)으로.</summary>
    void CursorColumnAbsolute(int col, CellAttributes attr);

    /// <summary>EL(ESC[K): 0=커서→끝, 1=시작→커서, 2=라인 전체.</summary>
    void EraseInLine(int mode);

    /// <summary>ED(ESC[J): 로그 모델에서는 스크롤백을 보존하고 새 라인으로 이동(2=화면 지움 대용).</summary>
    void EraseInDisplay(int mode);

    /// <summary>BEL: 벨(Phase A는 무시 또는 시각 신호).</summary>
    void Bell();

    /// <summary>DSR(ESC[6n) 응답용 현재 커서 위치(1-기준 행/열).</summary>
    (int Row, int Col) GetCursorPosition();
}
