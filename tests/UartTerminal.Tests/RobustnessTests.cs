using System.Text;
using UartTerminal.Core.Config;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

/// <summary>
/// "이상한 입력이 와도 앱이 계속 쓸 수 있어야 한다" 는 계약. 전부 실측으로 발견한 회귀다 —
/// 잘못된 baud 로 열면 임의 바이트가 들어오고, 개행 모드가 장치와 어긋나면 개행이 아예 오지 않는다.
/// </summary>
public sealed class RobustnessTests
{
    private static TerminalEngine Feed(string s, ReceiveNewline nl = ReceiveNewline.CrLf)
    {
        var e = new TerminalEngine(new UTF8Encoding(false)) { ReceiveNewline = nl };
        e.Receive(Encoding.UTF8.GetBytes(s));
        return e;
    }

    private static string Dump(TerminalEngine e)
    {
        var sb = new StringBuilder();
        lock (e.Buffer.SyncRoot)
            for (int i = 0; i < e.Buffer.LineCount; i++)
                sb.Append('|').Append(e.Buffer.GetLine(i).Text());
        return sb.ToString();
    }

    // ── OSC 탈출구 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 종료자 없는 OSC 뒤의 로그가 살아 있어야 한다. 예전에는 <c>ESC]</c> 하나로
    /// 그 뒤 모든 수신이 사라져(5MB 를 전부 삼켰다) 재연결 전까지 화면이 멈췄다.
    /// </summary>
    [Fact]
    public void UnterminatedOsc_DoesNotSwallowFollowingOutput()
    {
        var e = Feed("before\n\u001b]0;window title\nI (123) app: hello\nI (124) app: world\n");
        string got = Dump(e);

        Assert.Contains("hello", got);
        Assert.Contains("world", got);
    }

    /// <summary>개행조차 없는 이진 쓰레기여도 길이 상한으로 빠져나와야 한다.</summary>
    [Fact]
    public void UnterminatedOsc_EscapesByLengthCap()
    {
        var e = Feed("\u001b]" + new string('x', 5000) + "AFTER");
        Assert.Contains("AFTER", Dump(e));
    }

    /// <summary>정상 OSC(BEL 종료)는 그대로 소비되어 화면에 새지 않아야 한다(회귀 방지).</summary>
    [Fact]
    public void TerminatedOsc_IsStillConsumed()
    {
        var e = Feed("a\u001b]0;titleb\n");
        Assert.Equal("|ab|", Dump(e));
    }

    // ── CSI 파라미터 상한 ───────────────────────────────────────────────────

    /// <summary>
    /// 세미콜론이 계속 와도 파라미터 리스트가 무한히 자라지 않아야 한다.
    /// (힙 사용량으로 검사하면 다른 테스트의 할당에 흔들려 순서 의존이 된다 — 실제로 그렇게 썼다가
    ///  단독 실행은 통과하고 전체 실행은 실패했다. 그래서 불변식을 직접 본다.)
    /// </summary>
    [Fact]
    public void UnboundedCsiParameters_AreCapped()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);

        lock (buffer.SyncRoot)
        {
            parser.Feed("\u001b[");
            for (int i = 0; i < 20; i++)
                parser.Feed(new string(';', 100_000));   // 세미콜론 200만개
        }

        Assert.True(parser.PendingParameterCount <= AnsiParser.MaxParams,
            $"파라미터 {parser.PendingParameterCount}개 — 상한 {AnsiParser.MaxParams} 을 넘었습니다");
    }

    /// <summary>실제로 쓰는 트루컬러 시퀀스(38;2;r;g;b)는 상한에 걸리지 않아야 한다(회귀 방지).</summary>
    [Fact]
    public void TrueColorSequence_StillApplies()
    {
        var e = Feed("\u001b[38;2;255;128;64mX");
        lock (e.Buffer.SyncRoot)
        {
            var cell = e.Buffer.GetLine(0)[0];
            Assert.Equal('X', cell.Ch);
            Assert.Equal(ColorKind.Rgb, cell.Attributes.Foreground.Kind);
            Assert.Equal(255, cell.Attributes.Foreground.R);
            Assert.Equal(128, cell.Attributes.Foreground.G);
            Assert.Equal(64, cell.Attributes.Foreground.B);
        }
    }

    // ── 논리 라인 길이 상한 ─────────────────────────────────────────────────

    /// <summary>
    /// 개행이 오지 않는 스트림에서 한 줄이 무한히 자라면 안 된다.
    /// 측정(수정 전): 8MB 입력 → 한 줄 7,000,000셀 · 힙 112MB.
    /// </summary>
    [Theory]
    [InlineData(ReceiveNewline.Cr, "\n")]   // Rx=CR 인데 장치는 LF 만 보냄
    [InlineData(ReceiveNewline.Lf, "\r")]   // Rx=LF 인데 장치는 CR 만 보냄
    public void MismatchedNewlineMode_DoesNotGrowOneLineForever(ReceiveNewline mode, string deviceEol)
    {
        var e = new TerminalEngine(new UTF8Encoding(false)) { ReceiveNewline = mode };
        var line = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("I (123456) wifi: sta connected" + deviceEol, 2000)));
        for (int i = 0; i < 20; i++) e.Receive(line);

        int widest = 0;
        lock (e.Buffer.SyncRoot)
            for (int i = 0; i < e.Buffer.LineCount; i++)
                widest = Math.Max(widest, e.Buffer.GetLine(i).Count);

        Assert.True(widest <= TerminalBuffer.MaxLineCells,
            $"최장 라인 {widest:N0}셀 — 상한 {TerminalBuffer.MaxLineCells:N0} 을 넘었습니다");
    }

    /// <summary>
    /// CR 로 되돌아가 덮어쓰는 진행률 표시는 강제 개행 대상이 아니다 —
    /// 줄이 자라지 않으므로 상한에 걸릴 이유가 없다(상한 도입이 이 동작을 깨지 않았는지 확인).
    /// </summary>
    [Fact]
    public void CarriageReturnOverwrite_DoesNotTriggerForcedLineBreak()
    {
        var sb = new StringBuilder();
        for (int i = 0; i <= 100; i++) sb.Append($"\rprogress: {i}%");
        var e = Feed(sb.ToString());

        int lines;
        lock (e.Buffer.SyncRoot) lines = e.Buffer.LineCount;
        Assert.Equal(1, lines);
        Assert.Contains("progress: 100%", Dump(e));
    }

    // ── 관용 enum 변환기 ────────────────────────────────────────────────────

    /// <summary>
    /// 손편집으로 어떤 값이 들어와도 <b>세션 목록 전체를 잃지 않아야</b> 한다.
    /// 배열/객체에서 <c>reader.Skip()</c> 을 빼먹어 "read too much or not enough" 예외가 나고,
    /// 그게 파싱 실패로 번져 sessions.json 이 .corrupt-* 로 격리됐다.
    /// </summary>
    [Theory]
    [InlineData("\"Cr\"")]
    [InlineData("\"Crr\"")]      // 오타
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("[\"Cr\"]")]     // 배열
    [InlineData("{\"v\":\"Cr\"}")] // 객체
    public void HandEditedNewlineValue_NeverLosesTheSessionList(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "uart-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "sessions.json");
            File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "sessions": [
                { "name": "board", "port": "COM7", "baud": 115200, "newlineTx": {{json}} }
              ]
            }
            """);

            var store = new SessionStore(path);
            store.Load();

            Assert.Single(store.Items);
            Assert.Equal("COM7", store.Items[0].Port);
            Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
