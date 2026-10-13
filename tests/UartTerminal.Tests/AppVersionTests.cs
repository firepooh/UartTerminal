using System.Text.RegularExpressions;

namespace UartTerminal.Tests;

/// <summary>
/// 제목 줄과 정보 창이 보여 주는 버전. 값 자체는 빌드 환경(git 태그, CI 의 -p:Version)이 정하므로
/// 여기서는 <b>모양</b>만 고정한다 — SDK 가 붙이는 "+커밋해시" 가 제목에 새면 안 된다.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void AppVersion_IsTagOrDevBuild_WithoutCommitHash()
    {
        string v = AboutDialog.AppVersion;

        // 1.15.0(태그 위) · 1.15.0-dev.4(태그 뒤 4커밋) · 1.0.0-dev.12(CI 일반 푸시)
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-dev\.\d+)?$"), v);
    }
}
