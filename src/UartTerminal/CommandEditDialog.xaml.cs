using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using UartTerminal.Core.Config;

namespace UartTerminal;

/// <summary>
/// 저장 명령 편집기. 평면 목록 + 상세(마스터-디테일)로, 폴더 트리·드래그 정렬은 두지 않는다
/// (명령 10~20개 규모에 트리는 과함 — 순서는 ↑↓ 버튼).
/// <b>모달</b>로 띄우기 때문에 편집 중 다른 경로(+저장 버튼/다른 창)의 동시 변경이 원천적으로 없고,
/// 열 때 파일을 다시 읽어 손편집도 반영한다.
/// </summary>
public partial class CommandEditDialog : Window
{
    /// <summary>편집 중인 한 항목. 목록 표시가 즉시 갱신되도록 INotifyPropertyChanged 로 바인딩한다.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private string _name = "";
        private string _text = "";
        private bool _confirm;

        public string Name { get => _name; set { _name = value ?? ""; Raise(); } }
        public string Text { get => _text; set { _text = value ?? ""; Raise(); } }
        public bool Confirm { get => _confirm; set { _confirm = value; Raise(); } }

        /// <summary>목록에 보일 문자열(이름이 비면 전송 문자열로 대체 — 저장 시 규칙과 동일).</summary>
        public string Display => _name.Length > 0 ? _name : (_text.Length > 0 ? _text : "(새 명령)");

        public Visibility ConfirmMark => _confirm ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>UI 자동화/스크린리더가 읽는 항목 이름(WPF 는 비문자열 항목에서 ToString 을 쓴다).</summary>
        public override string ToString() => Display;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConfirmMark)));
        }
    }

    private static bool _open; // 모달이라 사실상 불필요하지만 재진입 방어

    private readonly CommandStore _store;
    private readonly ObservableCollection<Row> _rows = new();

    private CommandEditDialog(CommandStore store)
    {
        InitializeComponent();
        _store = store;
        PathText.Text = store.FilePath;

        foreach (var c in store.Items)
            _rows.Add(new Row { Name = c.Name, Text = c.Text, Confirm = c.Confirm });

        CmdList.ItemsSource = _rows;
        if (_rows.Count > 0) CmdList.SelectedIndex = 0;
        UpdateButtons();
    }

    /// <summary>편집기를 모달로 띄운다(앱 전역 1개). 열기 전에 파일을 다시 읽어 외부 변경을 반영.</summary>
    public static void ShowEditor(CommandStore store, Window? owner)
    {
        if (_open) return;

        store.Load();
        if (store.LastError is { } err)
            MessageBox.Show(owner, err, "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);

        _open = true;
        try
        {
            var dlg = new CommandEditDialog(store);
            if (owner is not null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
        }
        finally
        {
            _open = false;
        }
    }

    // ── 목록 편집 ────────────────────────────────────────────────────────────────

    private void CmdList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailPane.DataContext = CmdList.SelectedItem;
        DetailPane.IsEnabled = CmdList.SelectedItem is not null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int i = CmdList.SelectedIndex;
        DeleteButton.IsEnabled = i >= 0;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _rows.Count - 1;
        AddButton.IsEnabled = _rows.Count < CommandStore.MaxCommands;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= CommandStore.MaxCommands) return;
        var row = new Row();
        _rows.Add(row);
        CmdList.SelectedItem = row;
        CmdList.ScrollIntoView(row);
        NameBox.Focus();
        UpdateButtons();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        int i = CmdList.SelectedIndex;
        if (i < 0) return;
        _rows.RemoveAt(i);
        if (_rows.Count > 0) CmdList.SelectedIndex = Math.Min(i, _rows.Count - 1);
        UpdateButtons();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int i = CmdList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        _rows.Move(i, j);
        CmdList.SelectedIndex = j;
        CmdList.ScrollIntoView(_rows[j]);
        UpdateButtons();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_store.FilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagLog.Warn($"명령 파일 폴더 열기 실패: {ex.Message}");
        }
    }

    // ── 저장 ─────────────────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 전송 문자열이 빈 항목은 저장되지 않는다(무의미) — 조용히 사라지지 않게 먼저 알린다.
        int empty = _rows.Count(r => r.Text.Trim().Length == 0);
        if (empty > 0)
        {
            var ask = MessageBox.Show(this,
                $"전송 문자열이 비어 있는 항목 {empty}개는 저장되지 않습니다. 계속할까요?",
                "UartTerminal", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;
        }

        var items = _rows.Select(r => new SavedCommand { Name = r.Name, Text = r.Text, Confirm = r.Confirm });
        if (!_store.ReplaceAll(items))
        {
            // 실패 시 창을 닫지 않는다(편집 내용을 잃지 않게).
            MessageBox.Show(this, _store.LastError ?? "명령을 저장하지 못했습니다.",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }
}
