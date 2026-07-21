using System.Windows;
using System.Windows.Controls;
using UartTerminal.Core.Serial;

namespace UartTerminal;

public partial class PortSelectDialog : Window
{
    public PortInfo? SelectedPort { get; private set; }

    public PortSelectDialog(string? preselectPort = null)
    {
        InitializeComponent();
        RefreshPorts(preselectPort);
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
        DialogResult = true;
    }
}
