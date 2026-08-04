using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using UartTerminal.Core.Flash;

namespace UartTerminal;

/// <summary>
/// 펌웨어 플래시 화면. zip 을 고르면 <see cref="FlashPackageAnalyzer"/> 가 칩·오프셋·파일을 뽑아
/// 표로 보여주고, 시작하면 <b>포트를 양보 → esptool 실행 → 재연결</b> 순으로 진행한다.
///
/// 포트 양보/복귀는 이미 검증된 경로(MCP uart_close/uart_open 과 동일한 컨트롤러 메서드)를
/// 콜백으로 받아 재사용한다 — 이 창은 시리얼을 직접 만지지 않는다.
/// </summary>
public partial class FlashDialog : Window
{
    /// <summary>표의 한 줄(체크·후보 선택이 바뀌면 UI 에 알린다).</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private bool _selected;
        private string? _fileName;

        public required uint Offset { get; init; }
        public required string Role { get; init; }
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

        /// <summary>후보별 크기(파일을 바꾸면 크기 표시도 따라가야 한다).</summary>
        public IReadOnlyDictionary<string, long> Sizes { get; init; } = new Dictionary<string, long>();

        public string? Note { get; init; }

        public string OffsetText => $"0x{Offset:X}";

        public bool Selected
        {
            get => _selected;
            set { _selected = value && CanSelect; Raise(); }
        }

        public string? FileName
        {
            get => _fileName;
            set { _fileName = value; Raise(); }
        }

        public bool CanSelect => _fileName is not null;

        public long Size => _fileName is not null && Sizes.TryGetValue(_fileName, out long s) ? s : 0;

        public string SizeText => _fileName is null ? "—" : $"{Size / 1024.0:N1} KB";

        public string FileDisplay => _fileName ?? "(패키지에 없음)";

        // 후보가 여럿일 때만 콤보를 보여준다.
        public Visibility ChoiceVisibility => Candidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlainVisibility => Candidates.Count > 1 ? Visibility.Collapsed : Visibility.Visible;

        public Brush NoteBrush => (Brush)Application.Current.Resources[
            Note is null ? "TextFaint" : _fileName is null ? "Red" : "TextFaint"];

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>바뀐 속성 + 그것에서 파생되는 표시 속성들을 함께 알린다(체크·크기·파일명이 서로 엮여 있다).</summary>
        private void Raise([CallerMemberName] string? prop = null)
        {
            var names = new List<string>
            {
                nameof(CanSelect), nameof(Size), nameof(SizeText),
                nameof(FileDisplay), nameof(NoteBrush), nameof(Selected),
            };
            if (prop is { Length: > 0 } && !names.Contains(prop)) names.Add(prop);

            foreach (string p in names)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
    }

    /// <summary>칩 콤보 항목(자동 = null).</summary>
    private static readonly (string Text, EspChip? Value)[] ChipItems =
    {
        ("자동 (패키지에서 판별)", null),
        ("ESP32", EspChip.Esp32),
        ("ESP32-S2", EspChip.Esp32S2),
        ("ESP32-S3", EspChip.Esp32S3),
        ("ESP32-C2", EspChip.Esp32C2),
        ("ESP32-C3", EspChip.Esp32C3),
        ("ESP32-C6", EspChip.Esp32C6),
        ("ESP32-H2", EspChip.Esp32H2),
        ("ESP32-P4", EspChip.Esp32P4),
    };

    private static readonly int[] BaudPresets = { 115200, 230400, 460800, 576000, 921600, 1152000 };

    private static bool _open;

    private readonly AppState _state;
    private readonly string _portName;
    private readonly Func<Task<bool>> _releasePort;
    private readonly Func<Task<bool>> _reopenPort;
    private readonly ObservableCollection<Row> _rows = new();

    private FlashPackage? _package;
    private EsptoolInfo? _tool;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _syncing;

    private FlashDialog(AppState state, string portName, Func<Task<bool>> releasePort, Func<Task<bool>> reopenPort)
    {
        InitializeComponent();
        _state = state;
        _portName = portName;
        _releasePort = releasePort;
        _reopenPort = reopenPort;

        PortText.Text = string.IsNullOrEmpty(portName) ? "(연결 없음)" : portName;
        ItemList.ItemsSource = _rows;

        _syncing = true;
        ChipBox.ItemsSource = ChipItems.Select(c => c.Text).ToList();
        ChipBox.SelectedIndex = 0;
        BaudBox.ItemsSource = BaudPresets;
        BaudBox.SelectedItem = BaudPresets.Contains(state.FlashBaud) ? state.FlashBaud : 576000;
        _syncing = false;

        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    /// <summary>모달로 띄운다. 이미 열려 있으면 무시(중복 플래시 방지).</summary>
    public static void ShowFlash(Window? owner, AppState state, string portName,
                                 Func<Task<bool>> releasePort, Func<Task<bool>> reopenPort)
    {
        if (_open) return;
        _open = true;
        try
        {
            var dlg = new FlashDialog(state, portName, releasePort, reopenPort);
            if (owner is not null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
        }
        finally { _open = false; }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // esptool 탐색은 프로세스를 띄우므로 UI 를 막지 않게 백그라운드에서.
        var search = EsptoolSearch.Default(_state.EsptoolPath);
        _tool = await Task.Run(() => EsptoolLocator.Resolve(search, p => EsptoolRunner.ProbeVersion(p)));

        if (_tool is null)
        {
            ToolText.Text = "esptool 을 찾지 못했습니다 — ESP-IDF 를 설치하거나 esptool 실행 파일을 지정하세요.";
            ToolText.Foreground = (Brush)Application.Current.Resources["Red"];
            AppendNotice("esptool 실행 파일이 없어 플래시할 수 없습니다.\n" +
                         "· ESP-IDF 가 설치된 PC 라면 자동으로 찾습니다.\n" +
                         "· 아니면 esptool 공식 릴리스(standalone)를 받아 앱 폴더의 tools\\esptool\\ 에 두거나 " +
                         "state.json 의 esptoolPath 에 경로를 적으세요.");
        }
        else
        {
            ToolText.Text = $"{_tool.VersionText}  ·  {_tool.Path}";
        }

        // 지난번 zip 이 그대로 있으면 바로 불러온다(반복 작업이 대부분이다).
        if (!string.IsNullOrEmpty(_state.LastFlashZip) && File.Exists(_state.LastFlashZip!))
            LoadPackage(_state.LastFlashZip!);

        UpdateButtons();
    }

    // ── zip 선택/해석 ────────────────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "펌웨어 패키지 선택",
            Filter = "펌웨어 패키지 (*.zip)|*.zip|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(_state.LastFlashZip))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(_state.LastFlashZip!); } catch { }
        }
        if (dlg.ShowDialog(this) != true) return;

        LoadPackage(dlg.FileName);
    }

    private void LoadPackage(string zipPath)
    {
        _rows.Clear();
        NoticePane.Visibility = Visibility.Collapsed;
        NoticeText.Text = "";
        ZipBox.Text = zipPath;

        try
        {
            using var src = new ZipFlashSource(zipPath);
            _package = FlashPackageAnalyzer.Analyze(src, zipPath, SelectedChipOverride());
        }
        catch (Exception ex)
        {
            _package = null;
            AppendNotice($"패키지를 열지 못했습니다: {ex.Message}");
            UpdateButtons();
            return;
        }

        // 후보별 크기 맵(파일을 바꾸면 크기 표시도 따라가게)
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var src = new ZipFlashSource(zipPath))
            foreach (var (name, size) in src.Files) sizes[name] = size;

        foreach (var item in _package.Items)
        {
            var row = new Row
            {
                Offset = item.Offset,
                Role = item.Role,
                Candidates = item.Candidates,
                Sizes = sizes,
                Note = item.Note,
            };
            row.FileName = item.FileName;   // CanSelect 가 확정된 뒤에 체크를 넣어야 한다
            row.Selected = item.Selected;
            _rows.Add(row);
        }

        // 판별된 칩을 콤보에 반영(사용자가 이미 고른 값은 유지).
        // 근거(헤더 chip_id 등)는 콤보 폭을 넘기므로 툴팁에 둔다.
        if (SelectedChipOverride() == EspChip.Unknown)
        {
            _syncing = true;
            var texts = ChipItems.Select(c => c.Text).ToList();
            texts[0] = _package.Chip == EspChip.Unknown
                ? "자동 (판별 실패 — 직접 고르세요)"
                : $"자동 — {_package.Chip.DisplayName()}";
            ChipBox.ItemsSource = texts;
            ChipBox.SelectedIndex = 0;
            ChipBox.ToolTip = _package.Chip == EspChip.Unknown
                ? "패키지에서 칩을 판별하지 못했습니다 — 직접 고르세요."
                : $"판별 근거: {_package.ChipSource}\nesptool --chip {_package.Chip.EsptoolName()}";
            _syncing = false;
        }

        foreach (string w in _package.Warnings) AppendNotice("경고: " + w);
        foreach (string er in _package.Errors) AppendNotice("오류: " + er);

        _state.LastFlashZip = zipPath;
        _state.Save();
        UpdateButtons();
    }

    private EspChip SelectedChipOverride()
    {
        int i = ChipBox.SelectedIndex;
        return i > 0 && i < ChipItems.Length ? ChipItems[i].Value ?? EspChip.Unknown : EspChip.Unknown;
    }

    private void ChipBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _running) return;
        // 칩을 바꾸면 오프셋 검증(부트로더 위치 등)이 달라지므로 다시 해석한다.
        if (!string.IsNullOrEmpty(ZipBox.Text)) LoadPackage(ZipBox.Text);
    }

    private void BaudBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || BaudBox.SelectedItem is not int b) return;
        _state.FlashBaud = b;
        _state.Save();
    }

    private void AppendNotice(string text)
    {
        NoticeText.Text = NoticeText.Text.Length == 0 ? text : NoticeText.Text + "\n" + text;
        NoticePane.Visibility = Visibility.Visible;
    }

    // ── 실행 ─────────────────────────────────────────────────────────────────

    private void UpdateButtons()
    {
        bool ready = !_running
                     && _tool is not null
                     && _package is { Errors.Count: 0 }
                     && _rows.Any(r => r.Selected && r.FileName is not null)
                     && !string.IsNullOrEmpty(_portName);
        StartButton.IsEnabled = ready;
        StopButton.IsEnabled = _running;
        CloseButton.IsEnabled = !_running;
        BrowseButton.IsEnabled = !_running;
        ChipBox.IsEnabled = !_running;
        BaudBox.IsEnabled = !_running;
        ItemList.IsEnabled = !_running;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _tool is null || _package is null) return;

        var selected = _rows.Where(r => r.Selected && r.FileName is not null).ToList();
        if (selected.Count == 0) return;

        // 같은 오프셋을 두 번 쓰는 조합은 여기서도 한 번 더 막는다(사용자가 후보를 바꿀 수 있으므로).
        var dup = selected.GroupBy(r => r.Offset).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            MessageBox.Show(this, $"오프셋 0x{dup.Key:X} 에 파일이 여러 개 선택됐습니다. 하나만 남기세요.",
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long total = selected.Sum(r => r.Size);
        var confirm = MessageBox.Show(this,
            $"{_portName} 에 {selected.Count}개 파일({total / 1024.0 / 1024.0:N2} MB)을 씁니다.\n" +
            $"칩: {(_package.Chip == EspChip.Unknown ? "자동" : _package.Chip.DisplayName())} · 속도: {BaudBox.SelectedItem}\n\n" +
            "진행하는 동안 이 탭의 연결이 잠시 끊깁니다. 계속할까요?",
            "펌웨어 플래시", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (confirm != MessageBoxResult.OK) return;

        _running = true;
        _cts = new CancellationTokenSource();
        LogBox.Clear();
        Progress.Value = 0;
        SetProgressText("준비 중…", 0);
        UpdateButtons();

        bool released = false;
        try
        {
            // 1) 압축 해제(재사용)
            string root = Path.Combine(AppState.Dir, "flash", FlashExtractor.WorkFolderName(ZipBox.Text));
            Log($"패키지 해제: {root}");
            var map = await Task.Run(() => FlashExtractor.Extract(ZipBox.Text, root), _cts.Token);

            var files = new List<(uint Offset, string File)>();
            foreach (var r in selected)
            {
                if (!map.TryGetValue(r.FileName!, out string? full))
                    throw new FileNotFoundException($"해제된 파일을 찾을 수 없습니다: {r.FileName}");
                files.Add((r.Offset, full));
            }

            // 2) 포트 양보
            SetProgressText("포트 양보 중…", 0);
            Log($"{_portName} 양보 요청");
            released = await _releasePort();
            if (!released)
            {
                Log("포트를 양보하지 못했습니다 — 중단합니다.");
                MessageBox.Show(this, "포트를 양보하지 못해 플래시를 시작할 수 없습니다.",
                    "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 3) esptool 실행
            var request = new FlashRequest
            {
                Port = _portName,
                Baud = BaudBox.SelectedItem is int b ? b : 576000,
                Chip = _package.Chip,
                Files = files,
                KeepFlashSettings = KeepBox.IsChecked == true,
                FlashMode = _package.Args.FlashMode,
                FlashFreq = _package.Args.FlashFreq,
                FlashSize = _package.Args.FlashSize,
            };
            var args = EsptoolCommand.BuildWriteFlash(request, _tool.UsesHyphenSyntax);
            Log(EsptoolCommand.ToDisplayLine(_tool.Path, args));
            Log("");

            var runner = new EsptoolRunner(_tool.Path);
            runner.Line += line => Dispatcher.BeginInvoke(() => Log(line));
            runner.Progress += p => Dispatcher.BeginInvoke(() =>
            {
                Progress.Value = p.Fraction;
                SetProgressText(p.Phase, p.Fraction);
            });

            var result = await runner.RunAsync(args, selected.Select(r => r.Size).ToList(), _cts.Token);

            if (result.Ok)
            {
                Progress.Value = 1;
                SetProgressText("완료", 1);
                Log("");
                Log("플래시 완료.");
            }
            else
            {
                SetProgressText(result.Canceled ? "중지됨" : "실패", Progress.Value);
                Log("");
                Log(result.Error ?? "실패");
            }
        }
        catch (OperationCanceledException)
        {
            SetProgressText("중지됨", Progress.Value);
            Log("취소되었습니다.");
        }
        catch (Exception ex)
        {
            SetProgressText("실패", Progress.Value);
            Log($"오류: {ex.Message}");
            DiagLog.Exception("Flash", ex);
        }
        finally
        {
            // 4) 포트 복귀 — 성공/실패/취소 어느 경우에도 되돌린다.
            if (released && ReconnectBox.IsChecked == true)
            {
                Log($"{_portName} 재연결…");
                bool ok = await _reopenPort();
                Log(ok ? "재연결됨." : "재연결 실패 — [터미널 > 재연결](Alt+N)로 다시 시도하세요.");
            }
            else if (released)
            {
                Log("포트는 양보된 상태입니다 — 필요하면 Alt+N 으로 재연결하세요.");
            }

            _cts?.Dispose();
            _cts = null;
            _running = false;
            UpdateButtons();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        Log("중지 요청…");
        try { _cts?.Cancel(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 플래시 중 창을 닫으면 프로세스가 남고 포트가 붕 뜬다.
        if (_running) e.Cancel = true;
    }

    private void SetProgressText(string phase, double fraction)
        => ProgressText.Text = $"{phase}  ·  {fraction * 100:0}%";

    private void Log(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
