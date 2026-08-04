using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using UartTerminal.Core;
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

        /// <summary>Core 가 준 번역 키 + 인자. 표시 문장은 <see cref="NoteText"/> 가 현재 언어로 만든다.</summary>
        public LocMessage? Note { get; init; }

        public string NoteText => Note is null ? "" : Loc.Format(Note);

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

        public string FileDisplay => _fileName ?? Loc.S("Flash.FileMissingCell");

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
    // static readonly 로 두면 앱 시작 시 언어로 고정된다 → 매번 조회하는 프로퍼티로 둔다.
    private static (string Text, EspChip? Value)[] ChipItems =>
    new (string, EspChip?)[]
    {
        (Loc.S("Flash.Chip.AutoGeneric"), null),
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

        PortText.Text = string.IsNullOrEmpty(portName) ? Loc.S("Flash.NoConnection") : portName;
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
            ToolText.Text = Loc.S("Flash.Tool.NotFound");
            ToolText.Foreground = (Brush)Application.Current.Resources["Red"];
            AppendNotice(Loc.S("Flash.Tool.NotFoundHelp"));
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
            Title = Loc.S("Flash.PickTitle"),
            Filter = Loc.S("Flash.PickFilter"),
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
            AppendNotice(Loc.F("Flash.Msg.OpenFailed", ex.Message));
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
                ? Loc.S("Flash.Chip.AutoFailed")
                : Loc.F("Flash.Chip.AutoDetected", _package.Chip.DisplayName());
            ChipBox.ItemsSource = texts;
            ChipBox.SelectedIndex = 0;
            ChipBox.ToolTip = _package.Chip == EspChip.Unknown
                ? Loc.S("Flash.Chip.TipUnknown")
                : Loc.F("Flash.Chip.Tip", _package.ChipSource, _package.Chip.EsptoolName());
            _syncing = false;
        }

        foreach (var w in _package.Warnings) AppendNotice(Loc.F("Flash.Prefix.Warn", Loc.Format(w)));
        foreach (var er in _package.Errors) AppendNotice(Loc.F("Flash.Prefix.Err", Loc.Format(er)));

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
            MessageBox.Show(this, Loc.F("Flash.Msg.DuplicateOffset", $"0x{dup.Key:X}"),
                "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // UI 값은 여기서 <b>전부 지역 변수로</b> 읽는다. 아래 작업은 백그라운드 스레드를 오가므로
        // 컨트롤을 직접 만지면 "다른 스레드가 이 개체를 소유하고 있어…" 예외가 난다.
        string zipPath = ZipBox.Text;
        int baud = BaudBox.SelectedItem is int b ? b : 576000;
        bool keepSettings = KeepBox.IsChecked == true;
        bool reconnectAfter = ReconnectBox.IsChecked == true;
        var chip = _package.Chip;
        var plan = selected.Select(r => (r.Offset, Name: r.FileName!, r.Size)).ToList();

        long total = plan.Sum(p => p.Size);
        var confirm = MessageBox.Show(this,
            Loc.F("Flash.Msg.Confirm", _portName, plan.Count, $"{total / 1024.0 / 1024.0:N2}",
                  chip == EspChip.Unknown ? Loc.S("Flash.Chip.Auto") : chip.DisplayName(), baud),
            Loc.S("Flash.Msg.ConfirmTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (confirm != MessageBoxResult.OK) return;

        _running = true;
        _cts = new CancellationTokenSource();
        LogBox.Clear();
        Progress.Value = 0;
        SetProgressText("Flash.Phase.Preparing", 0);
        UpdateButtons();

        bool released = false;
        try
        {
            // 1) 압축 해제(재사용)
            string root = Path.Combine(AppState.Dir, "flash", FlashExtractor.WorkFolderName(zipPath));
            Log(Loc.F("Flash.Log.Extract", root));
            var map = await Task.Run(() => FlashExtractor.Extract(zipPath, root), _cts.Token);

            var files = new List<(uint Offset, string File)>();
            foreach (var p in plan)
            {
                if (!map.TryGetValue(p.Name, out string? full))
                    throw new FileNotFoundException(Loc.F("Flash.Msg.ExtractMissing", p.Name));
                files.Add((p.Offset, full));
            }

            // 2) 포트 양보
            SetProgressText("Flash.Phase.Releasing", 0);
            Log(Loc.F("Flash.Log.ReleaseRequest", _portName));
            released = await _releasePort();
            if (!released)
            {
                Log(Loc.S("Flash.Log.ReleaseFailed"));
                MessageBox.Show(this, Loc.S("Flash.Msg.ReleaseFailedBody"),
                    "UartTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 3) esptool 실행
            var request = new FlashRequest
            {
                Port = _portName,
                Baud = baud,
                Chip = chip,
                Files = files,
                KeepFlashSettings = keepSettings,
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

            var result = await runner.RunAsync(args, plan.Select(p => p.Size).ToList(), _cts.Token);

            if (result.Ok)
            {
                Progress.Value = 1;
                SetProgressText("Flash.Phase.Done", 1);
                Log("");
                Log(Loc.S("Flash.Log.Done"));
            }
            else
            {
                SetProgressText(result.Canceled ? "Flash.Phase.Stopped" : "Flash.Phase.Failed", Progress.Value);
                Log("");
                Log(Loc.FormatOrNull(result.Error) ?? Loc.S("Flash.Log.Failed"));
            }
        }
        catch (OperationCanceledException)
        {
            SetProgressText("Flash.Phase.Stopped", Progress.Value);
            Log(Loc.S("Flash.Log.Canceled"));
        }
        catch (Exception ex)
        {
            SetProgressText("Flash.Phase.Failed", Progress.Value);
            Log(Loc.F("Flash.Log.Error", ex.Message));
            DiagLog.Exception("Flash", ex);
        }
        finally
        {
            // 4) 포트 복귀 — 성공/실패/취소 어느 경우에도 되돌린다.
            if (released && reconnectAfter)
            {
                Log(Loc.F("Flash.Log.Reconnecting", _portName));
                bool ok = await _reopenPort();
                Log(ok ? Loc.S("Flash.Log.Reconnected") : Loc.S("Flash.Log.ReconnectFailed"));
            }
            else if (released)
            {
                Log(Loc.S("Flash.Log.StillReleased"));
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
        Log(Loc.S("Flash.Log.StopRequested"));
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

    // 아래 둘은 백그라운드에서 불릴 수 있는 자리(프로세스 출력 펌프 등)라 스스로 UI 스레드로 넘긴다.
    // 호출자가 마샬링을 잊어도 "다른 스레드가 이 개체를 소유하고 있어…" 로 죽지 않게 하는 보험이다.

    /// <summary><paramref name="phaseKey"/> 는 Core 가 준 번역 키다 — 문장은 여기서 만든다.</summary>
    private void SetProgressText(string phaseKey, double fraction)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetProgressText(phaseKey, fraction));
            return;
        }
        ProgressText.Text = $"{Loc.S(phaseKey)}  ·  {fraction * 100:0}%";
    }

    private void Log(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Log(line));
            return;
        }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
