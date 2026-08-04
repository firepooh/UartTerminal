namespace UartTerminal.Core.Flash;

/// <summary>ESP 칩 종류(<c>esp_app_format.h</c> 의 <c>esp_chip_id_t</c> 와 대응).</summary>
public enum EspChip
{
    Unknown = -1,
    Esp32 = 0x0000,
    Esp32S2 = 0x0002,
    Esp32C3 = 0x0005,
    Esp32S3 = 0x0009,
    Esp32C2 = 0x000C,
    Esp32C6 = 0x000D,
    Esp32H2 = 0x0010,
    Esp32P4 = 0x0012,
}

/// <summary>이미지 헤더에서 읽어낸 정보(칩 + SPI 플래시 설정).</summary>
public readonly record struct EspImageInfo(EspChip Chip, string FlashMode, string FlashFreq, string FlashSize);

/// <summary>
/// ESP 애플리케이션/부트로더 이미지 헤더(24바이트) 파서.
/// zip 안에 칩 정보가 없는 경우가 많은데(<c>flash_project_args</c> 에는 --chip 이 없다),
/// <b>bootloader.bin 헤더의 chip_id 로 칩을 확정</b>할 수 있어 사용자가 고르지 않아도 된다.
///
/// <c>esp_image_header_t</c> 레이아웃:
/// <code>
///   0      magic (0xE9)
///   1      segment_count
///   2      spi_mode      0=QIO 1=QOUT 2=DIO 3=DOUT 4=FAST_READ 5=SLOW_READ
///   3      low nibble  = spi_speed  (0=40m 1=26m 2=20m 0xF=80m)
///          high nibble = spi_size   (0=1MB 1=2MB 2=4MB 3=8MB 4=16MB 5=32MB 6=64MB 7=128MB)
///   4..7   entry_addr
///   8      wp_pin
///   9..11  spi_pin_drv
///   12..13 chip_id      ← 칩 판별
///   14     min_chip_rev
/// </code>
/// </summary>
public static class EspImageHeader
{
    /// <summary>헤더 판별에 필요한 최소 바이트 수.</summary>
    public const int MinLength = 16;

    private const byte Magic = 0xE9;

    public static bool TryParse(ReadOnlySpan<byte> head, out EspImageInfo info)
    {
        info = default;
        if (head.Length < MinLength || head[0] != Magic) return false;

        int chipId = head[12] | (head[13] << 8);
        info = new EspImageInfo(
            Chip: Enum.IsDefined(typeof(EspChip), chipId) ? (EspChip)chipId : EspChip.Unknown,
            FlashMode: ModeName(head[2]),
            FlashFreq: FreqName(head[3] & 0x0F),
            FlashSize: SizeName((head[3] >> 4) & 0x0F));
        return true;
    }

    /// <summary>esptool 이 <c>--flash_mode</c> 로 받는 이름.</summary>
    private static string ModeName(byte v) => v switch
    {
        0 => "qio",
        1 => "qout",
        2 => "dio",
        3 => "dout",
        4 => "fast_read",
        5 => "slow_read",
        _ => "unknown",
    };

    private static string FreqName(int v) => v switch
    {
        0 => "40m",
        1 => "26m",
        2 => "20m",
        0xF => "80m",
        _ => "unknown",
    };

    private static string SizeName(int v) => v switch
    {
        0 => "1MB",
        1 => "2MB",
        2 => "4MB",
        3 => "8MB",
        4 => "16MB",
        5 => "32MB",
        6 => "64MB",
        7 => "128MB",
        _ => "unknown",
    };

    /// <summary>사람이 읽는 칩 이름.</summary>
    public static string DisplayName(this EspChip chip) => chip switch
    {
        EspChip.Esp32 => "ESP32",
        EspChip.Esp32S2 => "ESP32-S2",
        EspChip.Esp32S3 => "ESP32-S3",
        EspChip.Esp32C2 => "ESP32-C2",
        EspChip.Esp32C3 => "ESP32-C3",
        EspChip.Esp32C6 => "ESP32-C6",
        EspChip.Esp32H2 => "ESP32-H2",
        EspChip.Esp32P4 => "ESP32-P4",
        _ => "Unknown",   // 칩 이름은 고유명사 — 번역하지 않는다
    };

    /// <summary>esptool <c>--chip</c> 인자 이름.</summary>
    public static string EsptoolName(this EspChip chip) => chip switch
    {
        EspChip.Esp32 => "esp32",
        EspChip.Esp32S2 => "esp32s2",
        EspChip.Esp32S3 => "esp32s3",
        EspChip.Esp32C2 => "esp32c2",
        EspChip.Esp32C3 => "esp32c3",
        EspChip.Esp32C6 => "esp32c6",
        EspChip.Esp32H2 => "esp32h2",
        EspChip.Esp32P4 => "esp32p4",
        _ => "auto",
    };

    /// <summary>
    /// 칩별 부트로더 오프셋. 칩과 오프셋이 어긋나면(예: ESP32 인데 0x0) 잘못된 zip/칩 선택이라
    /// 경고로 알린다 — 실제로 쓸 오프셋은 <c>flash_project_args</c> 값을 따른다.
    /// </summary>
    public static uint? BootloaderOffset(this EspChip chip) => chip switch
    {
        EspChip.Esp32 or EspChip.Esp32S2 => 0x1000u,
        EspChip.Esp32P4 => 0x2000u,
        EspChip.Unknown => null,
        _ => 0x0u, // S3 / C2 / C3 / C6 / H2
    };
}
