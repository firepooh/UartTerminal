using System.Text;
using UartTerminal.Core.Serial;

namespace UartTerminal.Tests;

public class RxRingBufferTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);
    private static string Str(byte[] b) => Encoding.ASCII.GetString(b);

    [Fact]
    public void ReadFromZero_ReturnsAll_AdvancesCursor()
    {
        var r = new RxRingBuffer(4096);
        r.Append(Bytes("hello"));

        var s = r.Read(0, 1024);
        Assert.Equal("hello", Str(s.Data));
        Assert.Equal(5, s.Cursor);
        Assert.Equal(0, s.Dropped);
        Assert.Equal(5, s.End);
    }

    [Fact]
    public void IncrementalRead_ReturnsOnlyNewData()
    {
        var r = new RxRingBuffer(4096);
        r.Append(Bytes("abc"));
        var s1 = r.Read(0, 1024);
        Assert.Equal("abc", Str(s1.Data));

        r.Append(Bytes("def"));
        var s2 = r.Read(s1.Cursor, 1024);
        Assert.Equal("def", Str(s2.Data));
        Assert.Equal(6, s2.Cursor);
        Assert.Equal(0, s2.Dropped);
    }

    [Fact]
    public void ReadAtEnd_ReturnsEmpty()
    {
        var r = new RxRingBuffer(4096);
        r.Append(Bytes("xyz"));
        var s = r.Read(3, 1024);
        Assert.Empty(s.Data);
        Assert.Equal(3, s.Cursor);
        Assert.Equal(3, s.End);
    }

    [Fact]
    public void MaxBytes_LimitsReturnedData()
    {
        var r = new RxRingBuffer(4096);
        r.Append(Bytes("abcdefghij"));
        var s = r.Read(0, 4);
        Assert.Equal("abcd", Str(s.Data));
        Assert.Equal(4, s.Cursor);
        Assert.True(s.Cursor < s.End);
    }

    [Fact]
    public void Overflow_DropsOldest_ReportsDroppedBytes()
    {
        var r = new RxRingBuffer(4096); // 최소 용량
        // 용량 초과로 채운다: 총 6000바이트
        var chunk = new byte[6000];
        for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)('A' + (i % 26));
        r.Append(chunk);

        Assert.Equal(6000, r.Total);
        Assert.Equal(4096, r.Count);
        Assert.Equal(6000 - 4096, r.Oldest);

        // 커서 0(이미 유실된 구간)에서 읽으면 dropped 로 보고하고 가장 오래된 위치로 당겨진다.
        var s = r.Read(0, 8192);
        Assert.Equal(6000 - 4096, s.Dropped);
        Assert.Equal(4096, s.Data.Length);
        Assert.Equal(6000, s.Cursor);
        Assert.Equal(6000, s.End);

        // 반환된 첫 바이트는 가장 오래된 보관 바이트(원본 인덱스 1904)와 일치해야 한다.
        Assert.Equal(chunk[6000 - 4096], s.Data[0]);
        Assert.Equal(chunk[^1], s.Data[^1]);
    }

    [Fact]
    public void WrapAround_PreservesByteOrder()
    {
        var r = new RxRingBuffer(4096);
        // 경계 근처까지 채우고, 경계를 넘겨 다시 쓴다.
        r.Append(new byte[4000]);           // total=4000, count=4000
        var tail = Bytes("WRAP-AROUND-CHECK");
        r.Append(tail);                     // total=4017, 앞 일부 유실, 랩 발생

        var s = r.Read(r.Total - tail.Length, 8192);
        Assert.Equal("WRAP-AROUND-CHECK", Str(s.Data));
        Assert.Equal(r.Total, s.Cursor);
    }

    [Fact]
    public void AppendSingleChunkLargerThanCapacity_KeepsLastBytes()
    {
        var r = new RxRingBuffer(4096);
        var big = new byte[10000];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i & 0xFF);
        r.Append(big);

        Assert.Equal(10000, r.Total);      // 총량은 도착 전체를 반영
        Assert.Equal(4096, r.Count);       // 보관은 마지막 capacity 만
        var s = r.Read(r.Oldest, 8192);
        Assert.Equal(4096, s.Data.Length);
        Assert.Equal(big[10000 - 4096], s.Data[0]);
        Assert.Equal(big[^1], s.Data[^1]);
    }

    [Fact]
    public void Clear_ResetsCursor()
    {
        var r = new RxRingBuffer(4096);
        r.Append(Bytes("data"));
        r.Clear();
        Assert.Equal(0, r.Total);
        Assert.Equal(0, r.Count);
        var s = r.Read(0, 1024);
        Assert.Empty(s.Data);
    }
}
