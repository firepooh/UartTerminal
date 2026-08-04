using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UartTerminal.Core.Config;

namespace UartTerminal;

/// <summary>
/// 저장 명령 편집기. 3열 마스터-디테일: <b>그룹(프로젝트) | 명령·폴더 | 상세</b>.
/// 폴더는 1단계까지만 허용하며(하위의 하위 없음 — 데이터 계층도 평탄화로 강제),
/// 목록 순서는 ↑↓ 버튼과 <b>드래그</b>로 바꾼다.
/// <b>모달</b>이라 편집 중 다른 경로(+저장 버튼/다른 창)의 동시 변경이 없고, 열 때 파일을 다시 읽어 손편집도 반영한다.
/// </summary>
public partial class CommandEditDialog : Window
{
    /// <summary>편집 중인 한 항목(명령 또는 폴더 또는 폴더의 하위). 목록 갱신을 위해 INotifyPropertyChanged.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private string _name = "";
        private string _text = "";
        private bool _confirm;

        public string Name { get => _name; set { _name = value ?? ""; Raise(); } }
        public string Text { get => _text; set { _text = value ?? ""; Raise(); } }
        public bool Confirm { get => _confirm; set { _confirm = value; Raise(); } }

        /// <summary>폴더(하위를 갖는 항목)인지. 하위 항목 자신은 false.</summary>
        public bool IsFolder { get; init; }

        /// <summary>폴더의 하위 항목인지(들여쓰기 표시 + 이동 범위 제한에 사용).</summary>
        public bool IsChild { get; set; }

        public string Display => _name.Length > 0 ? _name : (_text.Length > 0 ? _text : IsFolder ? "(새 폴더)" : "(새 명령)");
        public string Glyph => IsFolder ? "▾" : (IsChild ? "└" : "•");
        public Thickness Indent => new(IsChild ? 16 : 0, 0, 0, 0);
        public Visibility ConfirmMark => _confirm ? Visibility.Visible : Visibility.Collapsed;

        public override string ToString() => Display;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConfirmMark)));
        }
    }

    /// <summary>편집 중인 한 그룹.</summary>
    private sealed class GroupRow
    {
        public string Name { get; set; } = "";
        /// <summary>이 그룹의 항목들(폴더는 바로 뒤에 자기 하위들이 IsChild=true 로 이어진다 — 평탄 표현).</summary>
        public ObservableCollection<Row> Rows { get; } = new();
        public override string ToString() => Name;
    }

    private static bool _open; // 모달이라 사실상 불필요하지만 재진입 방어

    private readonly CommandStore _store;
    private readonly ObservableCollection<GroupRow> _groups = new();
    private GroupRow? _current;

    private CommandEditDialog(CommandStore store)
    {
        InitializeComponent();
        _store = store;
        PathText.Text = store.FilePath;

        foreach (var g in store.Groups)
        {
            var gr = new GroupRow { Name = g.Name };
            foreach (var c in g.Commands)
            {
                if (c.IsFolder)
                {
                    gr.Rows.Add(new Row { Name = c.Name, Text = "", Confirm = c.Confirm, IsFolder = true });
                    foreach (var s in c.Items!)
                        gr.Rows.Add(new Row { Name = s.Name, Text = s.Text, Confirm = s.Confirm, IsChild = true });
                }
                else
                {
                    gr.Rows.Add(new Row { Name = c.Name, Text = c.Text, Confirm = c.Confirm });
                }
            }
            _groups.Add(gr);
        }
        if (_groups.Count == 0)
            _groups.Add(new GroupRow { Name = CommandStore.DefaultGroupName });

        GroupList.ItemsSource = _groups;
        GroupList.SelectedIndex = 0;
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

    // ── 그룹 ─────────────────────────────────────────────────────────────────────

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = GroupList.SelectedItem as GroupRow;
        CmdList.ItemsSource = _current?.Rows;
        CmdHeader.Text = _current is null ? "명령" : $"명령 — {_current.Name}";
        if (_current is { Rows.Count: > 0 }) CmdList.SelectedIndex = 0;
        UpdateButtons();
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_groups.Count >= CommandStore.MaxGroups)
        {
            MessageBox.Show(this, Loc.F("Cmd.GroupLimit", CommandStore.MaxGroups), "UartTerminal",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? name = TextPromptDialog.Ask(this, Loc.S("Cmd.AddGroupTitle"), Loc.S("Cmd.AddGroupPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var gr = new GroupRow { Name = name.Trim() };
        _groups.Add(gr);
        GroupList.SelectedItem = gr;
        GroupList.ScrollIntoView(gr);
        UpdateButtons();
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        string? name = TextPromptDialog.Ask(this, Loc.S("Cmd.RenameGroupTitle"), Loc.S("Cmd.RenameGroupPrompt"), _current.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _current.Name = name.Trim();
        // ObservableCollection 은 항목 '내부' 변경을 모른다 → 표시 갱신을 위해 다시 바인딩
        int i = GroupList.SelectedIndex;
        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _groups;
        GroupList.SelectedIndex = i;
        CmdHeader.Text = $"명령 — {_current.Name}";
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (_groups.Count <= 1)
        {
            MessageBox.Show(this, Loc.S("Cmd.LastGroup"), "UartTerminal",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = MessageBox.Show(this, Loc.F("Cmd.ConfirmDeleteGroup", _current.Name),
            "UartTerminal", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return;

        int i = _groups.IndexOf(_current);
        _groups.Remove(_current);
        GroupList.SelectedIndex = Math.Min(i, _groups.Count - 1);
        UpdateButtons();
    }

    // ── 명령/폴더 편집 ───────────────────────────────────────────────────────────

    private void CmdList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = CmdList.SelectedItem as Row;
        DetailPane.DataContext = row;
        DetailPane.IsEnabled = row is not null;
        // 폴더는 전송 문자열이 없다 — 입력을 감추는 대신 비활성화해 이유를 안내한다.
        bool folder = row?.IsFolder == true;
        TextBoxCmd.IsEnabled = !folder;
        TextLabel.Opacity = folder ? 0.45 : 1.0;
        FolderHint.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var rows = _current?.Rows;
        int i = CmdList.SelectedIndex;
        var sel = CmdList.SelectedItem as Row;

        DeleteButton.IsEnabled = i >= 0;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = rows is not null && i >= 0 && i < rows.Count - 1;
        AddButton.IsEnabled = rows is not null && TopLevelCount(rows) < CommandStore.MaxCommands;
        AddFolderButton.IsEnabled = AddButton.IsEnabled;
        // 하위 추가는 폴더 또는 그 하위가 선택돼 있을 때(해당 폴더에 붙인다)
        AddSubButton.IsEnabled = sel is not null && (sel.IsFolder || sel.IsChild);
        DeleteGroupButton.IsEnabled = _groups.Count > 1;
        RenameGroupButton.IsEnabled = _current is not null;
        AddGroupButton.IsEnabled = _groups.Count < CommandStore.MaxGroups;
    }

    private static int TopLevelCount(IEnumerable<Row> rows) => rows.Count(r => !r.IsChild);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var row = new Row();
        _current.Rows.Add(row); // 최상위로 끝에 추가
        SelectRow(row);
        NameBox.Focus();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var row = new Row { IsFolder = true, Name = "" };
        _current.Rows.Add(row);
        SelectRow(row);
        NameBox.Focus();
    }

    private void AddSub_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || CmdList.SelectedItem is not Row sel) return;

        // 선택이 폴더면 그 폴더, 하위면 그 하위가 속한 폴더를 찾는다.
        int folderIdx = FolderIndexOf(_current.Rows, CmdList.SelectedIndex);
        if (folderIdx < 0) return;

        int insert = folderIdx + 1;
        while (insert < _current.Rows.Count && _current.Rows[insert].IsChild) insert++; // 폴더의 마지막 하위 뒤
        int subCount = insert - folderIdx - 1;
        if (subCount >= CommandStore.MaxSubCommands)
        {
            MessageBox.Show(this, Loc.F("Cmd.SubLimit", CommandStore.MaxSubCommands), "UartTerminal",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var row = new Row { IsChild = true };
        _current.Rows.Insert(insert, row);
        SelectRow(row);
        NameBox.Focus();
    }

    /// <summary>주어진 인덱스가 속한 폴더의 인덱스(자신이 폴더면 자신). 폴더에 속하지 않으면 -1.</summary>
    private static int FolderIndexOf(IList<Row> rows, int index)
    {
        if (index < 0 || index >= rows.Count) return -1;
        if (rows[index].IsFolder) return index;
        if (!rows[index].IsChild) return -1;
        for (int i = index - 1; i >= 0; i--)
            if (rows[i].IsFolder) return i;
        return -1;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        int i = CmdList.SelectedIndex;
        if (i < 0) return;

        var row = _current.Rows[i];
        if (row.IsFolder)
        {
            // 폴더 삭제 시 하위도 함께(사용자에게 확인)
            int end = i + 1;
            while (end < _current.Rows.Count && _current.Rows[end].IsChild) end++;
            int subs = end - i - 1;
            if (subs > 0)
            {
                var r = MessageBox.Show(this, Loc.F("Cmd.ConfirmDeleteFolder", subs), "UartTerminal",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (r != MessageBoxResult.OK) return;
            }
            for (int k = end - 1; k >= i; k--) _current.Rows.RemoveAt(k);
        }
        else
        {
            _current.Rows.RemoveAt(i);
        }

        if (_current.Rows.Count > 0) CmdList.SelectedIndex = Math.Min(i, _current.Rows.Count - 1);
        UpdateButtons();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    /// <summary>선택 항목 이동. 폴더는 하위와 함께 블록으로 움직이고, 하위는 자기 폴더 안에서만 움직인다.</summary>
    private void MoveSelected(int delta)
    {
        if (_current is null) return;
        var rows = _current.Rows;
        int i = CmdList.SelectedIndex;
        if (i < 0) return;
        var row = rows[i];

        if (row.IsChild)
        {
            int j = i + delta;
            // 같은 폴더의 하위 범위 내에서만
            if (j < 0 || j >= rows.Count || !rows[j].IsChild) return;
            rows.Move(i, j);
            CmdList.SelectedIndex = j;
        }
        else
        {
            // 최상위 블록(폴더+하위) 단위 이동
            var (start, len) = BlockAt(rows, i);
            if (delta < 0)
            {
                if (start == 0) return;
                var (pstart, plen) = BlockAt(rows, PrevTopIndex(rows, start));
                MoveBlock(rows, start, len, pstart);
                CmdList.SelectedIndex = pstart;
            }
            else
            {
                int next = start + len;
                if (next >= rows.Count) return;
                var (nstart, nlen) = BlockAt(rows, next);
                MoveBlock(rows, nstart, nlen, start); // 다음 블록을 앞으로 = 현재 블록이 뒤로
                CmdList.SelectedIndex = start + nlen;
            }
        }
        CmdList.ScrollIntoView(CmdList.SelectedItem);
        UpdateButtons();
    }

    /// <summary>index 가 속한 최상위 블록(폴더+하위 또는 단일 명령)의 (시작, 길이).</summary>
    private static (int start, int len) BlockAt(IList<Row> rows, int index)
    {
        int start = index;
        while (start > 0 && rows[start].IsChild) start--;
        int len = 1;
        while (start + len < rows.Count && rows[start + len].IsChild) len++;
        return (start, len);
    }

    private static int PrevTopIndex(IList<Row> rows, int start)
    {
        int i = start - 1;
        while (i > 0 && rows[i].IsChild) i--;
        return Math.Max(0, i);
    }

    private static void MoveBlock(ObservableCollection<Row> rows, int start, int len, int dest)
    {
        var block = new List<Row>();
        for (int k = 0; k < len; k++) block.Add(rows[start + k]);
        for (int k = len - 1; k >= 0; k--) rows.RemoveAt(start + k);
        int at = dest > start ? dest - len : dest;
        for (int k = 0; k < len; k++) rows.Insert(at + k, block[k]);
    }

    private void SelectRow(Row row)
    {
        CmdList.SelectedItem = row;
        CmdList.ScrollIntoView(row);
        UpdateButtons();
    }

    // ── 드래그로 순서 변경 ───────────────────────────────────────────────────────

    private Point _dragStart;
    private object? _dragItem;
    private ListBox? _dragSource;

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var item = ItemUnderMouse(list, e.OriginalSource as DependencyObject);
        if (item is null) return;
        _dragStart = e.GetPosition(list);
        _dragItem = item;
        _dragSource = list;
    }

    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || !ReferenceEquals(_dragSource, sender)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragItem = null; return; }

        var list = (ListBox)sender;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        try { DragDrop.DoDragDrop(list, _dragItem, DragDropEffects.Move); }
        catch (Exception ex) { DiagLog.Warn($"드래그 정렬 실패: {ex.Message}"); }
        finally { _dragItem = null; }
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void GroupList_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem is not GroupRow src) return;
        var target = ItemUnderMouse(GroupList, e.OriginalSource as DependencyObject) as GroupRow;
        int from = _groups.IndexOf(src);
        int to = target is null ? _groups.Count - 1 : _groups.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        _groups.Move(from, to);
        GroupList.SelectedItem = src;
    }

    private void CmdList_Drop(object sender, DragEventArgs e)
    {
        if (_current is null || _dragItem is not Row src) return;
        var rows = _current.Rows;
        var target = ItemUnderMouse(CmdList, e.OriginalSource as DependencyObject) as Row;

        int from = rows.IndexOf(src);
        if (from < 0) return;
        int to = target is null ? rows.Count - 1 : rows.IndexOf(target);
        if (to < 0 || from == to) return;

        if (src.IsChild)
        {
            // 하위는 같은 폴더 안에서만 이동(다른 폴더로 옮기려면 삭제 후 추가 — 규칙을 단순하게 유지)
            int srcFolder = FolderIndexOf(rows, from);
            int dstFolder = FolderIndexOf(rows, to);
            if (srcFolder < 0 || srcFolder != dstFolder || !rows[to].IsChild) return;
            rows.Move(from, to);
            CmdList.SelectedItem = src;
        }
        else
        {
            // 최상위(폴더 블록 포함) 이동. 타깃이 하위면 그 폴더 블록 경계로 스냅.
            var (sstart, slen) = BlockAt(rows, from);
            var (dstart, _) = BlockAt(rows, to);
            if (dstart == sstart) return;
            MoveBlock(rows, sstart, slen, dstart);
            CmdList.SelectedItem = src;
        }
        UpdateButtons();
    }

    /// <summary>마우스 아래의 ListBoxItem 이 담고 있는 데이터 항목(없으면 null).</summary>
    private static object? ItemUnderMouse(ListBox list, DependencyObject? source)
    {
        while (source is not null && source != list)
        {
            if (source is ListBoxItem lbi) return lbi.DataContext;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
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
        // 저장되지 않는 항목(전송 문자열 빈 명령 / 하위 없는 폴더)을 먼저 알린다.
        int dropped = 0;
        foreach (var g in _groups)
        {
            var rows = g.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.IsFolder)
                {
                    int subs = 0;
                    for (int k = i + 1; k < rows.Count && rows[k].IsChild; k++)
                        if (rows[k].Text.Trim().Length > 0) subs++;
                    if (subs == 0) dropped++;
                }
                else if (r.Text.Trim().Length == 0) dropped++;
            }
        }
        if (dropped > 0)
        {
            var ask = MessageBox.Show(this,
                $"전송 문자열이 비어 있는 명령 또는 하위가 없는 폴더 {dropped}개는 저장되지 않습니다. 계속할까요?",
                "UartTerminal", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;
        }

        var groups = _groups.Select(g => new CommandGroup { Name = g.Name, Commands = BuildCommands(g.Rows) });
        if (!_store.ReplaceAllGroups(groups))
        {
            // 실패 시 창을 닫지 않는다(편집 내용을 잃지 않게).
            MessageBox.Show(this, _store.LastError ?? Loc.S("Cmd.SaveFailed"),
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }

    /// <summary>평탄한 편집 행(폴더 + IsChild 하위)을 중첩된 SavedCommand 목록으로 되돌린다.</summary>
    private static List<SavedCommand> BuildCommands(IList<Row> rows)
    {
        var list = new List<SavedCommand>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.IsChild) continue; // 폴더 처리에서 함께 소비

            if (r.IsFolder)
            {
                var subs = new List<SavedCommand>();
                for (int k = i + 1; k < rows.Count && rows[k].IsChild; k++)
                {
                    var s = rows[k];
                    subs.Add(new SavedCommand { Name = s.Name, Text = s.Text, Confirm = s.Confirm });
                }
                list.Add(new SavedCommand { Name = r.Name, Text = "", Confirm = r.Confirm, Items = subs });
            }
            else
            {
                list.Add(new SavedCommand { Name = r.Name, Text = r.Text, Confirm = r.Confirm });
            }
        }
        return list;
    }
}
