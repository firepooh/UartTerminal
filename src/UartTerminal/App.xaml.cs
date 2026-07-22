using System.Windows;

namespace UartTerminal;

/// <summary>
/// 앱 시작 로직. 메인 ShellWindow 를 만들고, 메인 창이 닫히면 앱을 종료한다
/// (분리된 떠다니는 창들도 함께 닫힘). 첫 탭의 포트 선택은 ShellWindow.OnLoaded 에서 진행.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var state = AppState.Load();
        var shell = new ShellWindow(state, isPrimary: true);
        MainWindow = shell;
        shell.Show();
    }
}
