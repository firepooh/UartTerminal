using System.ComponentModel;
using System.Windows.Data;
using UartTerminal;

namespace UartTerminal.Tests;

/// <summary>
/// 표시 언어(한/영) 회귀 테스트. 번역 누락과 접근키 충돌은 눈으로 못 잡으므로 여기서 막는다.
/// </summary>
public sealed class LocTests : IDisposable
{
    public LocTests() => Loc.SetLanguage(AppLanguage.Korean);

    /// <summary>Loc 은 전역 상태다 — 테스트가 서로를 오염시키지 않게 되돌린다.</summary>
    public void Dispose() => Loc.SetLanguage(AppLanguage.Korean);

    [Fact]
    public void EveryKey_HasEnglishTranslation()
    {
        // 비어 있으면 한국어로 떨어지므로 화면이 섞인다 → 처음부터 못 들어오게 막는다.
        var missing = Loc.MissingTranslations();
        Assert.True(missing.Count == 0, $"영문 번역 누락: {string.Join(", ", missing)}");
    }

    [Fact]
    public void UnknownKey_IsVisibleNotSilent()
    {
        // 조용히 빈 문자열을 돌려주면 라벨이 사라져 원인을 못 찾는다.
        Assert.Equal("[없는.키]", Loc.S("없는.키"));
    }

    [Fact]
    public void SwitchingLanguage_ChangesStrings()
    {
        string ko = Loc.S("Menu.Terminal");
        Loc.SetLanguage(AppLanguage.English);
        string en = Loc.S("Menu.Terminal");

        Assert.Equal("터미널(_T)", ko);
        Assert.Equal("_Terminal", en);
    }

    [Fact]
    public void SwitchingLanguage_NotifiesIndexer_SoBindingsRefresh()
    {
        // 이 알림 하나로 화면의 모든 {loc:Str} 바인딩이 다시 평가된다 —
        // 재시작하지 않고 언어를 바꿀 수 있는 근거이므로 계약으로 고정한다.
        var seen = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => seen.Add(e.PropertyName);
        ((INotifyPropertyChanged)Loc.Current).PropertyChanged += handler;
        try
        {
            Loc.SetLanguage(AppLanguage.English);
        }
        finally
        {
            ((INotifyPropertyChanged)Loc.Current).PropertyChanged -= handler;
        }

        Assert.Contains(Binding.IndexerName, seen);
    }

    [Fact]
    public void SameLanguage_DoesNotNotify()
    {
        int count = 0;
        PropertyChangedEventHandler handler = (_, _) => count++;
        ((INotifyPropertyChanged)Loc.Current).PropertyChanged += handler;
        try { Loc.SetLanguage(AppLanguage.Korean); }
        finally { ((INotifyPropertyChanged)Loc.Current).PropertyChanged -= handler; }

        Assert.Equal(0, count);
    }

    [Fact]
    public void Indexer_MatchesStaticLookup()
    {
        Assert.Equal(Loc.S("Menu.Edit"), Loc.Current["Menu.Edit"]);
    }

    // ── 접근키(mnemonic) 규칙 ───────────────────────────────────────────────

    private static readonly string[] TopLevelMenuKeys =
    {
        "Menu.Terminal", "Menu.Edit", "Menu.View", "Menu.Board",
        "Menu.Window", "Menu.Command", "Menu.Mcp", "Menu.Help",
    };

    /// <summary>전역 Alt 단축키가 가로채는 글자 — 최상위 접근키로 쓰면 메뉴가 열리지 않는다.</summary>
    private static readonly char[] ReservedByGlobalShortcuts = { 'N', 'I', 'B', 'R' };

    private static char? Mnemonic(string text)
    {
        int i = text.IndexOf('_');
        return i >= 0 && i + 1 < text.Length ? char.ToUpperInvariant(text[i + 1]) : null;
    }

    [Theory]
    [InlineData(AppLanguage.Korean)]
    [InlineData(AppLanguage.English)]
    public void TopLevelMnemonics_AreUnique_AndAvoidGlobalShortcuts(AppLanguage language)
    {
        Loc.SetLanguage(language);

        var used = new Dictionary<char, string>();
        foreach (string key in TopLevelMenuKeys)
        {
            string text = Loc.S(key);
            char? m = Mnemonic(text);
            Assert.True(m is not null, $"{language}: '{text}' 에 접근키(_X)가 없습니다");

            Assert.False(ReservedByGlobalShortcuts.Contains(m!.Value),
                $"{language}: '{text}' 의 _{m} 는 전역 Alt+{m} 가 먼저 처리해 메뉴가 열리지 않습니다");

            Assert.False(used.ContainsKey(m.Value),
                $"{language}: 접근키 _{m} 중복 — '{used.GetValueOrDefault(m.Value)}' vs '{text}'");
            used[m.Value] = text;
        }
    }

    [Fact]
    public void TopLevelMnemonics_AreSameInBothLanguages()
    {
        // 언어를 바꿔도 Alt+T·Alt+E 같은 손가락 기억이 유지되어야 한다.
        var ko = new List<char?>();
        var en = new List<char?>();
        Loc.SetLanguage(AppLanguage.Korean);
        foreach (string k in TopLevelMenuKeys) ko.Add(Mnemonic(Loc.S(k)));
        Loc.SetLanguage(AppLanguage.English);
        foreach (string k in TopLevelMenuKeys) en.Add(Mnemonic(Loc.S(k)));

        Assert.Equal(ko, en);
    }
}
