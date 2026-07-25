using System.Windows;

namespace UartTerminal;

/// <summary>한 줄 입력을 받는 최소 프롬프트(세션 이름 등). 빈 입력은 확인을 거부한다.</summary>
public partial class TextPromptDialog : Window
{
    public string Value { get; private set; } = "";

    private TextPromptDialog(string prompt, string initial)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>확인 시 입력 문자열, 취소 시 null 을 반환.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var dlg = new TextPromptDialog(prompt, initial) { Title = title };
        if (owner is not null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string v = InputBox.Text.Trim();
        if (v.Length == 0)
        {
            InputBox.Focus();
            return; // 빈 이름은 받지 않는다(포트명으로 대체되는 혼란 방지)
        }
        Value = v;
        DialogResult = true;
    }
}
