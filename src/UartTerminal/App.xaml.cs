using System.IO;
using System.Windows;
using UartTerminal.Core.Config;

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

        // 저장 명령은 창 좌표 같은 휘발성 상태(state.json)와 분리된 사용자 저작 파일이다(팀 공유 = 이 파일 복사).
        var commands = new CommandStore(Path.Combine(AppState.Dir, "commands.json"));
        commands.Load();
        if (commands.LastError is { } err) DiagLog.Warn(err);

        var shell = new ShellWindow(state, commands, isPrimary: true);
        MainWindow = shell;
        shell.Show();
    }
}
