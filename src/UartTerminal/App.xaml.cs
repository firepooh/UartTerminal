using System.IO;
using System.Windows;
using System.Windows.Threading;
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

        // 전역 예외 그물: 미처리 예외를 진단 로그에 남기고, UI 스레드 예외는 앱을 죽이지 않고 계속(개발 도구 특성).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ea) =>
        {
            if (ea.ExceptionObject is Exception ex) DiagLog.Exception("AppDomain.Unhandled", ex);
        };

        var state = AppState.Load();
        DiagLog.Capture = state.DiagCapture; // 진단 캡처 설정 복원

        // 저장 명령은 창 좌표 같은 휘발성 상태(state.json)와 분리된 사용자 저작 파일이다(팀 공유 = 이 파일 복사).
        var commands = new CommandStore(Path.Combine(AppState.Dir, "commands.json"));
        var sessions = new SessionStore(Path.Combine(AppState.Dir, "sessions.json"));
        // 설정 파일 문제로 앱이 창 하나 없이 죽는 일이 없도록 시작 경로를 감싼다(둘 다 손편집 대상이다).
        try
        {
            commands.Load();
            if (commands.LastError is { } err) DiagLog.Warn(err);
        }
        catch (Exception ex)
        {
            DiagLog.Exception("CommandStore.Load", ex);
        }
        try
        {
            sessions.Load();
            if (sessions.LastError is { } err) DiagLog.Warn(err);
        }
        catch (Exception ex)
        {
            DiagLog.Exception("SessionStore.Load", ex);
        }

        var shell = new ShellWindow(state, commands, sessions, isPrimary: true);
        MainWindow = shell;
        shell.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagLog.Exception("Dispatcher.Unhandled", e.Exception);
        try
        {
            MessageBox.Show(
                $"예기치 못한 오류가 발생했지만 계속 실행합니다.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                @"자세한 내용: %LOCALAPPDATA%\UartTerminal\diag.log",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 알림 실패는 무시 */ }
        e.Handled = true; // 로그를 남긴 뒤 가능한 한 앱을 살려둔다
    }
}
