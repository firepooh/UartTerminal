using System.Text;

namespace UartTerminal.Core.Terminal;

/// <summary>
/// ANSI/VT 이스케이프 시퀀스 파서(상태 머신). README §4.1 지원 범위:
///  - 반영: SGR 컬러(16색+bright, bold/dim/italic/underline/reverse, reset), CR/LF/BS/HT
///  - esp_console(linenoise)용 예외: EL(ESC[K), 수평 커서 이동(CUF/CUB/CHA), DSR 커서 질의 응답
///  - 그 외(커서 절대이동 CUP, 스크롤 영역, alternate screen 등)는 "완전히 소비 후 무시"
///    → 반쪽 처리로 화면이 깨진 문자로 오염되지 않게 한다.
/// UI와 분리된 순수 로직이라 단위 테스트로 회귀를 방어한다.
/// </summary>
public sealed class AnsiParser
{
    private const char ESC = '';
    private const char BEL = '';
    private const char DEL = '';

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEsc }

    private readonly ITerminalSink _sink;
    private State _state = State.Ground;
    private CellAttributes _attr = CellAttributes.Default;

    /// <summary>직전에 처리한 개행 문자('\r'/'\n', 없으면 '\0'). CR+LF 쌍을 한 번으로 합치는 데 쓴다.</summary>
    private char _lastNewline;

    // CSI 파라미터 수집
    private readonly List<int> _params = new(8);
    private int _curParam;
    private bool _hasCurParam;
    private bool _privatePrefix; // '?'

    /// <summary>
    /// CSI 파라미터 개수 상한. 실제 시퀀스는 5개를 넘지 않는다(38;2;r;g;b 가 최대).
    /// 상한이 없던 동안 <c>ESC[</c> 뒤에 세미콜론이 계속 오면 리스트가 무한히 자랐다
    /// (측정: 세미콜론 400만개 → 힙 16.6MB). 잘못된 baud 의 임의 바이트로도 도달할 수 있다.
    /// </summary>
    public const int MaxParams = 32;

    /// <summary>
    /// OSC 문자열 최대 길이. 종료자(BEL/ST)가 오지 않으면 그 뒤 <b>모든 출력이 사라졌다</b> —
    /// 색 없는 평문 로그에서는 다음 ESC 도 오지 않으므로 재연결 전까지 화면이 영구히 멈췄다
    /// (측정: 5MB 를 전부 삼킴). 실제 OSC(창 제목 등)는 수백 바이트를 넘지 않는다.
    /// </summary>
    private const int MaxOscLength = 1024;

    private int _oscLength;

    /// <summary>DSR 응답 등 터미널→호스트 송신이 필요할 때 호출(엔진이 시리얼 TX로 연결).</summary>
    public Action<ReadOnlyMemory<byte>>? Respond { get; set; }

    /// <summary>수신 개행 규약. 기본 <see cref="ReceiveNewline.CrLf"/>(개행=LF, CR=줄 처음으로).</summary>
    public ReceiveNewline ReceiveNewline { get; set; } = ReceiveNewline.CrLf;

    public AnsiParser(ITerminalSink sink) => _sink = sink;

    public CellAttributes CurrentAttributes => _attr;

    /// <summary>
    /// 현재 수집 중인 CSI 파라미터 개수(진단·테스트용). 항상 <see cref="MaxParams"/> 이하여야 한다 —
    /// 상한이 없던 동안 <c>ESC[</c> 뒤 세미콜론만으로 리스트가 무한히 자랐다.
    /// </summary>
    public int PendingParameterCount => _params.Count;

    /// <summary>재연결 등에서 상태 초기화.</summary>
    public void Reset()
    {
        _state = State.Ground;
        _attr = CellAttributes.Default;
        _lastNewline = '\0';
        _oscLength = 0;
        ResetParams();
    }

    public void Feed(ReadOnlySpan<char> chars)
    {
        for (int i = 0; i < chars.Length; i++)
            Step(chars[i]);
    }

    private void Step(char ch)
    {
        switch (_state)
        {
            case State.Ground: Ground(ch); break;
            case State.Escape: Escape(ch); break;
            case State.EscapeIntermediate: EscapeIntermediate(ch); break;
            case State.Csi: Csi(ch); break;
            case State.Osc: Osc(ch); break;
            case State.OscEsc: OscEsc(ch); break;
        }
    }

    private void Ground(char ch)
    {
        // CR/LF 이외의 문자가 오면 개행 쌍(CR+LF/LF+CR) 추적을 끊는다 — 붙어 있는 쌍만 합치기 위해.
        if (ch != '\r' && ch != '\n') _lastNewline = '\0';

        switch (ch)
        {
            case ESC: _state = State.Escape; return;
            case '\n': FeedLf(); return;
            case '\r': FeedCr(); return;
            case '\b': _sink.Backspace(); return;
            case '\t': _sink.HorizontalTab(_attr); return;
            case BEL: _sink.Bell(); return;
            case DEL: return; // DEL 무시
        }
        if (ch < 0x20)
            return; // 기타 C0 제어(VT/FF/SI/SO 등) 무시
        _sink.Print(ch, _attr);
    }

    /// <summary>수신 CR 처리 — 모드에 따라 '개행' 또는 '줄 처음으로' 또는 무시.</summary>
    private void FeedCr()
    {
        switch (ReceiveNewline)
        {
            case ReceiveNewline.Cr:
                NewLine();
                break;
            case ReceiveNewline.Auto:
                if (_lastNewline == '\n') { _lastNewline = '\0'; return; } // LF+CR = 개행 1회(이미 처리됨)
                NewLine();
                break;
            case ReceiveNewline.Lf:
                break; // CR 무시
            default:
                _sink.CarriageReturn(); // CR+LF 모드: 줄 처음으로(덮어쓰기)
                break;
        }
        _lastNewline = '\r';
    }

    /// <summary>수신 LF 처리 — 모드에 따라 '개행' 또는 무시.</summary>
    private void FeedLf()
    {
        switch (ReceiveNewline)
        {
            case ReceiveNewline.Cr:
                break; // 개행은 CR 담당 — LF 무시(CRLF 가 두 줄이 되지 않게)
            case ReceiveNewline.Auto:
                if (_lastNewline == '\r') { _lastNewline = '\0'; return; } // CR+LF = 개행 1회(이미 처리됨)
                NewLine();
                break;
            default:
                _sink.LineFeed(); // CR+LF / LF 모드: LF 가 개행
                break;
        }
        _lastNewline = '\n';
    }

    /// <summary>개행 1회(줄 처음으로 + 새 줄). 논리 라인 모델에서 LineFeed 가 새 줄을 만든다.</summary>
    private void NewLine()
    {
        _sink.CarriageReturn();
        _sink.LineFeed();
    }

    private void Escape(char ch)
    {
        switch (ch)
        {
            case '[': ResetParams(); _state = State.Csi; return;
            case ']': _oscLength = 0; _state = State.Osc; return;
            case '(': case ')': case '*': case '+':
                _state = State.EscapeIntermediate; return; // 문자셋 지정: 다음 한 글자 소비
            case 'c': // RIS: 속성만 리셋(화면 전체 리셋은 로그 모델에서 과함)
                _attr = CellAttributes.Default;
                _state = State.Ground; return;
            default:
                // ESC 7/8/=/>/D/E/M 등 미지원 → 소비 후 Ground
                _state = State.Ground; return;
        }
    }

    private void EscapeIntermediate(char ch)
    {
        // 문자셋 지정 문자(B, 0, A 등) 한 글자를 소비하고 종료
        _state = State.Ground;
    }

    private void Csi(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            _curParam = _curParam * 10 + (ch - '0');
            _hasCurParam = true;
            return;
        }
        if (ch == ';')
        {
            if (_params.Count < MaxParams)
                _params.Add(_hasCurParam ? _curParam : 0);
            _curParam = 0;
            _hasCurParam = false;
            return;
        }
        if (ch == '?' || ch == '<' || ch == '=' || ch == '>')
        {
            // private/prefix 마커(예: ESC[?25h) — 표시만 하고 계속 수집
            if (ch == '?') _privatePrefix = true;
            return;
        }
        if (ch >= 0x20 && ch <= 0x2F)
        {
            // 중간 바이트(intermediate) — 무시하고 계속 수집
            return;
        }
        if (ch >= 0x40 && ch <= 0x7E)
        {
            // 최종 바이트 → 디스패치
            if ((_hasCurParam || _params.Count > 0) && _params.Count < MaxParams)
                _params.Add(_hasCurParam ? _curParam : 0);
            DispatchCsi(ch);
            _state = State.Ground;
            return;
        }
        // 예상 밖 → 중단
        _state = State.Ground;
    }

    private void DispatchCsi(char final)
    {
        switch (final)
        {
            case 'm': ApplySgr(); break;
            case 'K': _sink.EraseInLine(Param(0, 0)); break;
            case 'J': _sink.EraseInDisplay(Param(0, 0)); break;
            case 'C': _sink.CursorForward(Param(0, 1), _attr); break;
            case 'D': _sink.CursorBack(Param(0, 1)); break;
            case 'G': _sink.CursorColumnAbsolute(Param(0, 1), _attr); break;
            case 'n':
                if (!_privatePrefix)
                {
                    // DSR: 6=커서 위치 질의(→ESC[row;colR), 5=장치 상태 질의(→ESC[0n, "정상").
                    // esp_console/linenoise 는 ESC[5n 프로브 응답으로 터미널 능력을 감지한다(§6 Q2).
                    if (Param(0, 0) == 6) RespondCursorPosition();
                    else if (Param(0, 0) == 5) RespondDeviceStatus();
                }
                break;
            // 미지원(수직 이동/화면 제어/모드 설정 등)은 소비 후 무시:
            // 'A','B'(CUU/CUD), 'H','f'(CUP), 'r'(DECSTBM), 'h','l'(모드), 'd','S','T','X','P','@','L','M' 등
            default:
                break;
        }
    }

    private void RespondCursorPosition()
    {
        if (Respond is null) return;
        var (row, col) = _sink.GetCursorPosition();
        string report = $"{ESC}[{row};{col}R";
        Respond(Encoding.ASCII.GetBytes(report));
    }

    /// <summary>DSR(ESC[5n) 응답: 터미널 정상(ESC[0n). linenoise 가 이 응답으로 이스케이프 지원을 확인.</summary>
    private void RespondDeviceStatus()
    {
        if (Respond is null) return;
        Respond(Encoding.ASCII.GetBytes($"{ESC}[0n"));
    }

    private void ApplySgr()
    {
        if (_params.Count == 0)
        {
            _attr = CellAttributes.Default;
            return;
        }

        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: _attr = CellAttributes.Default; break;
                case 1: _attr = _attr.AddFlag(CellFlags.Bold); break;
                case 2: _attr = _attr.AddFlag(CellFlags.Dim); break;
                case 3: _attr = _attr.AddFlag(CellFlags.Italic); break;
                case 4: _attr = _attr.AddFlag(CellFlags.Underline); break;
                case 7: _attr = _attr.AddFlag(CellFlags.Reverse); break;
                case 22: _attr = _attr.RemoveFlag(CellFlags.Bold).RemoveFlag(CellFlags.Dim); break;
                case 23: _attr = _attr.RemoveFlag(CellFlags.Italic); break;
                case 24: _attr = _attr.RemoveFlag(CellFlags.Underline); break;
                case 27: _attr = _attr.RemoveFlag(CellFlags.Reverse); break;

                case >= 30 and <= 37: _attr = _attr.WithForeground(TermColor.FromPalette(p - 30)); break;
                case 39: _attr = _attr.WithForeground(TermColor.Default); break;
                case >= 40 and <= 47: _attr = _attr.WithBackground(TermColor.FromPalette(p - 40)); break;
                case 49: _attr = _attr.WithBackground(TermColor.Default); break;
                case >= 90 and <= 97: _attr = _attr.WithForeground(TermColor.FromPalette(8 + (p - 90))); break;
                case >= 100 and <= 107: _attr = _attr.WithBackground(TermColor.FromPalette(8 + (p - 100))); break;

                case 38:
                    i = ApplyExtendedColor(i, foreground: true);
                    break;
                case 48:
                    i = ApplyExtendedColor(i, foreground: false);
                    break;

                default:
                    break; // 미지원 SGR 무시
            }
        }
    }

    /// <summary>38/48 확장 색상(38;5;n = 256색, 38;2;r;g;b = 트루컬러) 처리. 소비한 마지막 인덱스를 반환.</summary>
    private int ApplyExtendedColor(int i, bool foreground)
    {
        if (i + 1 >= _params.Count) return i;
        int mode = _params[i + 1];
        if (mode == 5 && i + 2 < _params.Count)
        {
            var color = TermColor.FromPalette(_params[i + 2]);
            _attr = foreground ? _attr.WithForeground(color) : _attr.WithBackground(color);
            return i + 2;
        }
        if (mode == 2 && i + 4 < _params.Count)
        {
            var color = TermColor.FromRgb((byte)_params[i + 2], (byte)_params[i + 3], (byte)_params[i + 4]);
            _attr = foreground ? _attr.WithForeground(color) : _attr.WithBackground(color);
            return i + 4;
        }
        return i + 1;
    }

    private void Osc(char ch)
    {
        // OSC(예: 창 제목 설정)는 내용을 무시하고 종료자만 감지: BEL 또는 ST(ESC \)
        if (ch == BEL) { _state = State.Ground; return; }
        if (ch == ESC) { _state = State.OscEsc; return; }

        // 탈출구 두 개 — 없으면 종료자가 오지 않는 OSC 하나가 이후 모든 출력을 삼킨다.
        //  1) 개행/CAN/SUB 는 OSC 를 끝낸다(xterm 도 C0 제어에서 중단한다). 개행은 다시 처리해
        //     로그 한 줄이 통째로 사라지지 않게 한다.
        if (ch == '\n' || ch == '\r' || ch == '\x18' || ch == '\x1A')
        {
            _state = State.Ground;
            _oscLength = 0;
            Ground(ch);
            return;
        }
        //  2) 길이 상한 — 이진 쓰레기에는 개행조차 없을 수 있다.
        if (++_oscLength > MaxOscLength)
        {
            _state = State.Ground;
            _oscLength = 0;
        }
    }

    private void OscEsc(char ch)
    {
        // ESC \ = ST(문자열 종료). 그 외는 그냥 종료로 간주.
        _state = State.Ground;
    }

    private int Param(int index, int defaultValue) =>
        index < _params.Count ? _params[index] : defaultValue;

    private void ResetParams()
    {
        _params.Clear();
        _curParam = 0;
        _hasCurParam = false;
        _privatePrefix = false;
    }
}
