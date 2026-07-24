using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace UartTerminal;

/// <summary>앱 이름·버전·런타임·저장소 링크를 보여주는 정보(About) 대화상자.</summary>
public partial class AboutDialog : Window
{
    private const string RepoUrl = "https://github.com/firepooh/UartTerminal";

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"버전 {AppVersion}";
        RuntimeText.Text = $".NET {Environment.Version}  ·  {RuntimeInformation.ProcessArchitecture}";
    }

    /// <summary>어셈블리 InformationalVersion 에서 빌드 해시(+…) 접미사를 뗀 표시용 버전.</summary>
    public static string AppVersion
    {
        get
        {
            string info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }

    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
        catch (Exception ex) { DiagLog.Warn($"링크 열기 실패: {ex.Message}"); }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
