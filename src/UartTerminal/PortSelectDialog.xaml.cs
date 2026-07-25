using System.Windows;
using System.Windows.Controls;
using UartTerminal.Core.Serial;

namespace UartTerminal;

public partial class PortSelectDialog : Window
{
    /// <summary>
    /// 선택 가능한 통신 속도(README §2). ESP-IDF 개발에서 실제로 쓰이는 값만 둔다:
    /// 74880=ROM 부트로더 출력(부트루프 진단), 115200=기본 콘솔, 230400/460800/921600=고속 로그.
    /// </summary>
    public static readonly int[] BaudPresets = { 74880, 115200, 230400, 460800, 921600 };

    public const int DefaultBaud = 115200;

    public PortInfo? SelectedPort { get; private set; }

    /// <summary>사용자가 고른 통신 속도. 취소 시 의미 없음.</summary>
    public int SelectedBaud { get; private set; } = DefaultBaud;

    public PortSelectDialog(string? preselectPort = null, int preselectBaud = DefaultBaud)
    {
        InitializeComponent();
        BuildBaudChips(preselectBaud);
        RefreshPorts(preselectPort);
    }

    /// <summary>속도 세그먼트를 프리셋에서 생성(RadioButton 이라 배타 선택은 프레임워크가 처리).</summary>
    private void BuildBaudChips(int preselect)
    {
        int selected = BaudPresets.Contains(preselect) ? preselect : DefaultBaud;
        foreach (int baud in BaudPresets)
        {
            var rb = new RadioButton
            {
                Content = baud.ToString(),
                Tag = baud,
                GroupName = "Baud",
                IsChecked = baud == selected,
                Style = (Style)FindResource("BaudChip"),
            };
            BaudHost.Children.Add(rb);
        }
        SelectedBaud = selected;
    }

    private int CheckedBaud()
    {
        foreach (var child in BaudHost.Children)
            if (child is RadioButton { IsChecked: true, Tag: int baud })
                return baud;
        return DefaultBaud;
    }

    private void RefreshPorts(string? preselect)
    {
        var ports = PortEnumerator.Enumerate();
        PortList.ItemsSource = ports;

        if (ports.Count == 0)
            return;

        PortInfo? match = null;
        if (!string.IsNullOrEmpty(preselect))
            match = ports.FirstOrDefault(p => string.Equals(p.PortName, preselect, StringComparison.OrdinalIgnoreCase));

        PortList.SelectedItem = match ?? ports[0];
        PortList.Focus();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        string? current = (PortList.SelectedItem as PortInfo)?.PortName;
        RefreshPorts(current);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void PortList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PortList.SelectedItem is PortInfo)
            Accept();
    }

    private void Accept()
    {
        if (PortList.SelectedItem is not PortInfo info)
        {
            MessageBox.Show(this, "포트를 선택하세요.", "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedPort = info;
        SelectedBaud = CheckedBaud();
        DialogResult = true;
    }
}
