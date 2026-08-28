using System.Windows;
using UartTerminal.Mcp;

namespace UartTerminal;

/// <summary>
/// [MCP] 메뉴의 사용 안내. 등록 <b>스코프</b>(user/local/project)는 Claude Code 쪽 개념이라
/// 앱 어디에도 힌트가 없어서, 무엇을 어디에 등록하는지 한 화면에 모아 설명한다.
///
/// <para>주소와 명령은 <b>실제 설정값</b>으로 채운다 — 안내문에 5300 을 박아 두면
/// <c>state.json</c> 에서 포트를 바꾼 사용자에게 틀린 것을 알려주게 된다.</para>
/// </summary>
public partial class McpGuideDialog : Window
{
    private readonly string _command;

    public McpGuideDialog(Window? owner)
    {
        InitializeComponent();
        Owner = owner;

        // 호스트가 아직 안 떴어도(노출 0) 안내는 보여야 하므로 설정 포트로 대체한다.
        var server = McpHost.Server;
        int port = server?.Port ?? AppState.Load().McpHttpPort;

        UrlBox.Text = server?.Endpoint ?? $"http://127.0.0.1:{port}/mcp";   // loc:data
        _command = server?.RegistrationCommand
                   ?? $"claude mcp add --transport http uart http://127.0.0.1:{port}/mcp";   // loc:data
        CommandBox.Text = _command;

        // 내용이 화면보다 길면 스크롤로 넘긴다. 창은 그 전까지 내용에 맞춰 자란다.
        MaxHeight = SystemParameters.WorkArea.Height - 80;
    }

    // 안내를 닫지 않고 띄운다 — 목록을 보고 다시 안내로 돌아오는 흐름이 자연스럽다.
    private void Api_Click(object sender, RoutedEventArgs e) => new McpApiDialog(this).ShowDialog();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_command); }
        catch (System.Exception ex) { DiagLog.Warn($"클립보드 설정 실패: {ex.Message}"); }
        DialogResult = true;
    }
}
