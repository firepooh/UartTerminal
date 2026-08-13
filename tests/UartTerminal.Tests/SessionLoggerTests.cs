using System.Text;
using UartTerminal.Core.Logging;
using UartTerminal.Core.Terminal;

namespace UartTerminal.Tests;

/// <summary>
/// 연속 로깅(SessionLogger) 계약.
/// 핵심은 <b>청크 경계</b>다 — 수신은 임의 바이트 위치에서 잘려 오므로, CR+LF 쌍이 두 청크로
/// 갈라져도 타임스탬프가 이중으로 찍히면 안 된다(같은 이유의 결함이 strip_ansi 에서 실제로 있었다).
/// </summary>
public sealed class SessionLoggerTests : IDisposable
{
    private readonly string _dir;

    /// <summary>고정 시계 — 스탬프가 결정적이어야 내용 비교가 가능하다.</summary>
    private static readonly DateTime T0 = new(2026, 1, 2, 3, 4, 5, 678);
    private const string Stamp = "[03:04:05.678] ";

    public SessionLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uart-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".log");

    private string RunChunks(bool timestamps, params string[] chunks)
    {
        string path = NewPath();
        using (var lg = new SessionLogger(path, timestamps, clock: () => T0, format: LogFormat.Raw))
        {
            foreach (string c in chunks)
                lg.Append(Encoding.UTF8.GetBytes(c));
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }

    // ── 타임스탬프 없음: 바이트 그대로 ─────────────────────────────────────────

    /// <summary>끄면 파일은 수신 바이트와 정확히 같아야 한다 — ANSI·CR·부분 청크 포함(재현용 원본).</summary>
    [Fact]
    public void WithoutTimestamps_FileIsByteFaithful()
    {
        string input = "[0;32mI (307) main: start[0m\r\nprogress: 1%\rprogress: 2%\r\n";
        string got = RunChunks(false, input[..10], input[10..17], input[17..]);
        Assert.Equal(input, got);
    }

    // ── 타임스탬프: 줄 시작마다 ────────────────────────────────────────────────

    [Fact]
    public void WithTimestamps_EachLineIsStamped()
    {
        string got = RunChunks(true, "abc\r\ndef\r\n");
        Assert.Equal($"{Stamp}abc\r\n{Stamp}def\r\n", got);
    }

    /// <summary>CR+LF 쌍이 청크 경계에서 갈라져도 스탬프는 한 번이어야 한다.</summary>
    [Fact]
    public void CrLfSplitAcrossChunks_StampsOnce()
    {
        string got = RunChunks(true, "abc\r", "\ndef");
        Assert.Equal($"{Stamp}abc\r\n{Stamp}def", got);
    }

    /// <summary>한 바이트씩 흘러 들어와도(최악의 분할) 결과가 같아야 한다.</summary>
    [Fact]
    public void ByteAtATime_ProducesSameOutput()
    {
        string input = "a\r\nb\nc\rd";
        string whole = RunChunks(true, input);
        string split = RunChunks(true, input.Select(ch => ch.ToString()).ToArray());
        Assert.Equal(whole, split);
    }

    /// <summary>CR 덮어쓰기(진행률 표시)는 각 세그먼트가 스탬프를 받는다 — 언제 갱신됐는지 남는다.</summary>
    [Fact]
    public void CrOverwriteSegments_AreEachStamped()
    {
        string got = RunChunks(true, "p:1%\rp:2%\r");
        Assert.Equal($"{Stamp}p:1%\r{Stamp}p:2%\r", got);
    }

    /// <summary>빈 줄(연속 개행)에는 스탬프를 넣지 않는다 — 내용 바이트 앞에서만.</summary>
    [Fact]
    public void EmptyLines_AreNotStamped()
    {
        string got = RunChunks(true, "a\n\n\nb");
        Assert.Equal($"{Stamp}a\n\n\n{Stamp}b", got);
    }

    /// <summary>UTF-8 멀티바이트(한글)가 스탬프 삽입 위치와 얽혀도 깨지지 않는다.</summary>
    [Fact]
    public void MultibyteContent_SurvivesStamping()
    {
        string got = RunChunks(true, "부팅\r\n완료\r\n");
        Assert.Equal($"{Stamp}부팅\r\n{Stamp}완료\r\n", got);
    }

    // ── 수명주기 ───────────────────────────────────────────────────────────────

    /// <summary>Dispose 후 Append 는 조용히 무시된다(재연결 경합 등에서 예외가 나면 수신이 다친다).</summary>
    [Fact]
    public void AppendAfterDispose_IsSilentlyIgnored()
    {
        string path = NewPath();
        var lg = new SessionLogger(path, false, clock: () => T0, format: LogFormat.Raw);
        lg.Append(Encoding.ASCII.GetBytes("before"));
        lg.Dispose();
        lg.Append(Encoding.ASCII.GetBytes("after"));   // 예외 없이 무시
        lg.Dispose();                                  // 멱등

        Assert.Equal("before", File.ReadAllText(path));
    }

    /// <summary>이어 쓰기(Append): 기존 내용 뒤에 붙고, 새로 쓰기는 덮는다.</summary>
    [Fact]
    public void AppendMode_PreservesExistingContent()
    {
        string path = NewPath();
        File.WriteAllText(path, "old\r\n");

        using (var lg = new SessionLogger(path, false, append: true, clock: () => T0, format: LogFormat.Raw))
            lg.Append(Encoding.ASCII.GetBytes("new"));
        Assert.Equal("old\r\nnew", File.ReadAllText(path));

        using (var lg = new SessionLogger(path, false, append: false, clock: () => T0, format: LogFormat.Raw))
            lg.Append(Encoding.ASCII.GetBytes("fresh"));
        Assert.Equal("fresh", File.ReadAllText(path));
    }

    /// <summary>
    /// 화면 버퍼 스냅샷은 <b>스탬프 없이</b> 실린다 — 그 줄들의 실제 수신 시각은 알 수 없으므로
    /// '지금'을 찍으면 거짓이 된다. 이후 라이브 수신부터 스탬프가 시작된다.
    /// </summary>
    [Fact]
    public void ScreenSnapshot_IsUnstamped_ThenLiveDataIsStamped()
    {
        string path = NewPath();
        using (var lg = new SessionLogger(path, true, clock: () => T0, format: LogFormat.Raw))
        {
            lg.AppendScreenSnapshot("past1\npast2");   // 개행으로 안 끝남 → CRLF 가 보충된다
            lg.Append(Encoding.ASCII.GetBytes("live\r\n"));
        }

        Assert.Equal($"past1\npast2\r\n{Stamp}live\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void BytesWritten_CountsTimestampBytesToo()
    {
        string path = NewPath();
        using var lg = new SessionLogger(path, true, clock: () => T0, format: LogFormat.Raw);
        lg.Append(Encoding.ASCII.GetBytes("ab"));

        Assert.Equal(Stamp.Length + 2, lg.BytesWritten);
    }

    /// <summary>기록 중에도 다른 프로세스가 읽을 수 있어야 한다(tail 사용성).</summary>
    [Fact]
    public void FileIsReadableWhileLogging()
    {
        string path = NewPath();
        using var lg = new SessionLogger(path, false, clock: () => T0, format: LogFormat.Raw);
        lg.Append(Encoding.ASCII.GetBytes("hello"));

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[16];
        int n = reader.Read(buf, 0, buf.Length);
        Assert.Equal("hello", Encoding.ASCII.GetString(buf, 0, n));   // Flush 정책 검증을 겸한다
    }

    // ── 화면 그대로(기본 형식) ────────────────────────────────────────────────
    // 실사용에서 원시 기록이 ESC[0;32m·ESC[6n·NUL 로 뒤덮여 읽을 수 없었다. 화면 모드는
    // 엔진이 <b>이미 해석한</b> 논리 라인을 쓰므로 파일이 화면과 같아진다.

    /// <summary>수신 바이트를 엔진에 먹이고, 완성된 줄만 로거로 흘려보내는 실사용 배선을 흉내낸다.</summary>
    private static void Feed(TerminalEngine engine, SessionLogger lg, string text)
    {
        engine.Receive(Encoding.UTF8.GetBytes(text));
        lg.AppendRenderedLines(engine.Buffer);
    }

    /// <summary>로거가 아직 파일을 쥐고 있을 때 읽는다(File.ReadAllText 의 기본 공유 모드로는 열리지 않는다).</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static TerminalEngine NewEngine() =>
        new(new UTF8Encoding(false), maxLines: 1000) { ReceiveNewline = ReceiveNewline.CrLf };

    [Fact]
    public void Screen_StripsAnsiAndWritesWhatTheScreenShows()
    {
        string path = NewPath();
        var engine = NewEngine();
        using (var lg = new SessionLogger(path, false, clock: () => T0))
        {
            lg.StartAt(engine.Buffer);
            Feed(engine, lg, "[0;32mI (03:26:58.559) sleep: console(1)[0m\r\n");
            Feed(engine, lg, "[0;32mI (03:26:59.275) netmgr: plmn 450:5[0m\r\n");
        }

        Assert.Equal("I (03:26:58.559) sleep: console(1)\r\n"
                   + "I (03:26:59.275) netmgr: plmn 450:5\r\n", File.ReadAllText(path));
    }

    /// <summary>진행 중인 줄은 <b>끝나야</b> 쓴다 — 반쪽 줄이 먼저 적히면 완성본과 두 번 남는다.</summary>
    [Fact]
    public void Screen_WritesLineOnlyWhenComplete()
    {
        string path = NewPath();
        var engine = NewEngine();
        using var lg = new SessionLogger(path, false, clock: () => T0);
        lg.StartAt(engine.Buffer);

        Feed(engine, lg, "par");
        Assert.Equal("", ReadShared(path));

        Feed(engine, lg, "tial\r\n");
        Assert.Equal("partial\r\n", ReadShared(path));
    }

    /// <summary>정지 시에는 진행 중이던 줄도 남긴다(꼬리를 잃지 않게).</summary>
    [Fact]
    public void Screen_FlushPartial_WritesTrailingLine()
    {
        string path = NewPath();
        var engine = NewEngine();
        using (var lg = new SessionLogger(path, false, clock: () => T0))
        {
            lg.StartAt(engine.Buffer);
            Feed(engine, lg, "done\r\nhalf");
            lg.AppendRenderedLines(engine.Buffer, flushPartial: true);
        }
        Assert.Equal("done\r\nhalf\r\n", File.ReadAllText(path));
    }

    /// <summary>CR 덮어쓰기(진행률 표시)는 <b>화면에 남은 최종 상태만</b> 기록된다.</summary>
    [Fact]
    public void Screen_CarriageReturnOverwrite_KeepsOnlyFinalText()
    {
        string path = NewPath();
        var engine = NewEngine();
        using (var lg = new SessionLogger(path, false, clock: () => T0))
        {
            lg.StartAt(engine.Buffer);
            Feed(engine, lg, "10%\r50%\r100%\r\n");
        }
        Assert.Equal("100%\r\n", File.ReadAllText(path));
    }

    /// <summary>타임스탬프는 줄마다 한 번, 줄 앞에 붙는다.</summary>
    [Fact]
    public void Screen_Timestamps_PrefixEachLineOnce()
    {
        string path = NewPath();
        var engine = NewEngine();
        using (var lg = new SessionLogger(path, true, clock: () => T0))
        {
            lg.StartAt(engine.Buffer);
            Feed(engine, lg, "a\r\n");
            Feed(engine, lg, "b\r\n");
        }
        Assert.Equal($"{Stamp}a\r\n{Stamp}b\r\n", File.ReadAllText(path));
    }

    /// <summary>화면 모드에서 원시 tee(Append)는 무시돼야 한다 — 안 그러면 같은 내용이 두 번 적힌다.</summary>
    [Fact]
    public void Screen_IgnoresRawAppend()
    {
        string path = NewPath();
        using (var lg = new SessionLogger(path, false, clock: () => T0))
            lg.Append(Encoding.ASCII.GetBytes("raw bytes"));
        Assert.Equal("", File.ReadAllText(path));
    }

    /// <summary>원시 모드에서는 반대로 화면 드레인이 아무것도 쓰지 않아야 한다.</summary>
    [Fact]
    public void Raw_IgnoresRenderedDrain()
    {
        string path = NewPath();
        var engine = NewEngine();
        using (var lg = new SessionLogger(path, false, clock: () => T0, format: LogFormat.Raw))
        {
            engine.Receive(Encoding.UTF8.GetBytes("hello\r\n"));
            lg.AppendRenderedLines(engine.Buffer, flushPartial: true);
        }
        Assert.Equal("", File.ReadAllText(path));
    }

    /// <summary>
    /// 스크롤백이 잘려도 같은 줄을 두 번 쓰거나 조용히 건너뛰지 않는다 —
    /// 진행 위치를 절대 라인 번호로 잡기 때문. 못 따라간 만큼은 세어서 드러낸다.
    /// </summary>
    [Fact]
    public void Screen_BufferTrim_DoesNotDuplicateOrSilentlySkip()
    {
        string path = NewPath();
        // 버퍼 하한이 100줄이라(TerminalBuffer 생성자) 그보다 넉넉히 넘겨야 실제로 잘린다
        var engine = new TerminalEngine(new UTF8Encoding(false), maxLines: 100)
        { ReceiveNewline = ReceiveNewline.CrLf };

        using (var lg = new SessionLogger(path, false, clock: () => T0))
        {
            lg.StartAt(engine.Buffer);
            // 드레인 없이 상한을 넘겨 밀어낸다(정상 사용에서는 매 수신마다 드레인된다)
            for (int i = 0; i < 300; i++)
                engine.Receive(Encoding.UTF8.GetBytes($"line{i}\r\n"));
            lg.AppendRenderedLines(engine.Buffer, flushPartial: true);

            Assert.True(lg.DroppedLines > 0, $"잘림을 세지 못했다(DroppedLines={lg.DroppedLines})");
        }

        var lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(lines.Length, lines.Distinct().Count());   // 중복 없음
        Assert.Contains("line299", lines);                      // 최신 줄은 남는다
    }
}
