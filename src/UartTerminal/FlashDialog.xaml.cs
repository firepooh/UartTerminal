using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UartTerminal.Core;
using UartTerminal.Core.Flash;

namespace UartTerminal;

/// <summary>
/// 펌웨어 플래시 화면. zip 이나 압축을 푼 폴더를 고르면 <see cref="FlashPackageAnalyzer"/> 가
/// 칩·오프셋·파일을 뽑아 표로 보여주고, 시작하면 <b>포트를 양보 → esptool 실행 → 재연결</b> 순으로 진행한다.
/// 같은 수명주기로 플래시 전체 지우기(erase_flash)도 한다.
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

    // 진행률 표시. 전체 지우기처럼 바이트 진행률이 없는 작업은 퍼센트 대신 경과 시간을 보인다 —
    // 수십 초 동안 "0%" 로 멈춰 있으면 멈춘 것처럼 보인다.
    private string _phaseKey = "Flash.Phase.Preparing";
    private Stopwatch? _elapsed;
    private DispatcherTimer? _elapsedTimer;

    // ── 영역 굽기 탭 ──
    /// <summary>주소 기본값. 안내문(Flash.RegionIntro)에도 명기돼 있다 — 바꿨다가 되돌릴 때 보라고.</summary>
    private const string DefaultRegionAddress = "0xD90000";

    private IReadOnlyList<string> _regionBins = Array.Empty<string>();   // 풀린 bin 전체 경로(BinBox 와 같은 순서)
    private bool _srcLoading;   // 다운로드/압축 해제 중(시작 버튼 잠금)

    private FlashDialog(AppState state, string portName, Func<Task<bool>> releasePort, Func<Task<bool>> reopenPort)
    {
        InitializeComponent();
        _state = state;
        _portName = portName;
        _releasePort = releasePort;
        _reopenPort = reopenPort;

        // 포트는 제목줄이 말한다 — 창을 연 순간 정해진 값이라 입력 영역에서 한 칸을 쓸 이유가 없다.
        Title = Loc.F("Flash.TitleWithPort",
                      string.IsNullOrEmpty(portName) ? Loc.S("Flash.NoConnection") : portName);
        ItemList.ItemsSource = _rows;

        _syncing = true;
        ChipBox.ItemsSource = ChipItems.Select(c => c.Text).ToList();
        ChipBox.SelectedIndex = 0;
        BaudBox.ItemsSource = BaudPresets;
        BaudBox.SelectedItem = BaudPresets.Contains(state.FlashBaud) ? state.FlashBaud : 576000;
        _syncing = false;

        // SizeToContent 로 커지되 화면 밖으로 나가지 않게 상한을 건다 — 넘치면 목록이 스크롤된다.
        MaxHeight = SystemParameters.WorkArea.Height - 60;

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

        // 지난번 zip(또는 폴더)이 그대로 있으면 바로 불러온다(반복 작업이 대부분이다).
        if (_state.LastFlashZip is { Length: > 0 } last && (File.Exists(last) || Directory.Exists(last)))
            LoadPackage(last);

        // 주소: 마지막으로 쓴 값, 없으면 기본값. 소스는 복원만 한다 —
        // 실제 불러오기는 영역 탭에 들어올 때(탭 전환 핸들러) 일어난다.
        AddrBox.Text = string.IsNullOrWhiteSpace(_state.LastRegionAddress)
            ? DefaultRegionAddress : _state.LastRegionAddress!;
        if (!string.IsNullOrEmpty(_state.LastRegionSource)) SrcBox.Text = _state.LastRegionSource!;

        UpdateButtons();
    }

    // ── 영역 굽기: 소스(bin/zip/http) 해석 ──────────────────────────────────────

    private void SrcBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.S("Flash.RegionSourcePick"),
            Filter = Loc.S("Flash.RegionSourceFilter"),
            CheckFileExists = true,
        };
        try
        {
            string cur = SrcBox.Text.Trim();
            if (cur.Length > 0 && !RegionSourceResolver.IsUrl(cur)
                && Path.GetDirectoryName(cur) is { Length: > 0 } dir && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch { }
        if (dlg.ShowDialog(this) != true) return;

        SrcBox.Text = dlg.FileName;
        _ = ResolveSourceAsync();
    }

    private void SrcLoad_Click(object sender, RoutedEventArgs e) => _ = ResolveSourceAsync();

    private void SrcBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = ResolveSourceAsync(); }
    }

    /// <summary>소스를 바꾸면 이미 준비된 BIN 은 낡은 것이다 — 비우고 [불러오기] 유도로 되돌린다.</summary>
    private void SrcBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_srcLoading || _running) return;
        if (_regionBins.Count > 0)
        {
            _regionBins = Array.Empty<string>();
            BinBox.ItemsSource = null;
        }
        UpdateButtons();
    }

    /// <summary>
    /// 소스를 로컬 bin 목록으로 푼다: http 링크면 내려받고, zip 이면 풀고(*.bin 만), bin 이면 그대로.
    /// bin 이 여럿이면 사용자가 고른다 — 잘못된 파일을 잘못된 주소에 구우면 장비가 손상되므로 추측하지 않는다.
    /// </summary>
    private async Task ResolveSourceAsync()
    {
        if (_srcLoading || _running) return;
        string input = SrcBox.Text.Trim();
        if (input.Length == 0) return;

        _srcLoading = true;
        _regionBins = Array.Empty<string>();
        BinBox.ItemsSource = null;
        BinInfoText.Text = "";
        UpdateButtons();
        try
        {
            string file = input;
            if (RegionSourceResolver.IsUrl(input))
            {
                Log(Loc.F("Flash.Log.Downloading", input));
                file = await RegionSourceResolver.DownloadAsync(
                    input, Path.Combine(AppState.Dir, "flash", "download"), CancellationToken.None);
                Log(Loc.F("Flash.Log.Downloaded", Path.GetFileName(file), new FileInfo(file).Length / 1024.0));
            }
            else if (!File.Exists(file))
            {
                AppendNotice(Loc.F("Flash.Msg.SourceMissing", file));
                return;
            }

            var bins = await Task.Run(() =>
                RegionSourceResolver.ResolveBins(file, Path.Combine(AppState.Dir, "flash")));

            _regionBins = bins;
            BinBox.ItemsSource = bins.Select(p => Path.GetFileName(p)).ToList();
            BinBox.SelectedIndex = bins.Count > 0 ? 0 : -1;
            if (bins.Count == 0) AppendNotice(Loc.S("Flash.Msg.NoBin"));
            else if (bins.Count > 1) Log(Loc.F("Flash.Log.PickBin", bins.Count));

            _state.LastRegionSource = input;
            _state.Save();
        }
        catch (Exception ex)
        {
            AppendNotice(Loc.F("Flash.Msg.SourceFailed", ex.Message));
            DiagLog.Exception("FlashRegionSource", ex);   // 점 없는 태그 — Loc 키 린트에 걸리지 않게
        }
        finally
        {
            _srcLoading = false;
            UpdateButtons();
        }
    }

    private string? SelectedBinPath =>
        BinBox.SelectedIndex >= 0 && BinBox.SelectedIndex < _regionBins.Count
            ? _regionBins[BinBox.SelectedIndex] : null;

    /// <summary>입력된 주소(파싱 성공 시). 실패면 null — 시작 버튼이 잠긴다.</summary>
    private uint? RegionAddress =>
        FlashAddress.TryParse(AddrBox.Text, out uint addr) ? addr : null;

    private void AddrBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        // 파싱 안 되는 주소는 즉시 색으로 알린다(시작 버튼이 왜 죽었는지 화면이 설명하게).
        AddrBox.Foreground = RegionAddress is null ? Theme.Brush("Red") : Theme.Brush("Text");
        if (RegionAddress is not null) { _state.LastRegionAddress = AddrBox.Text.Trim(); _state.Save(); }
        UpdateButtons();
    }

    private void BinBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateButtons();   // 크기/유도 문구는 UpdateButtons 가 상태에 맞게 채운다

    private string? _autoResolvedSource;   // 탭 전환 자동 불러오기의 재시도 폭주 방지(같은 소스는 1회만)

    private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ModeTabs)) return;

        // 영역 탭에 들어오면 마지막 소스가 유효할 때 바로 불러오기까지 진행한다 —
        // "탭 열기 → 시작" 두 클릭이 반복 작업의 전부가 되게. 같은 소스는 한 번만
        // (죽은 링크로 탭을 오갈 때마다 재다운로드·오류 반복을 막는다). 실패해도 [불러오기]로 수동 재시도.
        if (ReferenceEquals(ModeTabs.SelectedItem, RegionTab)
            && !_running && !_srcLoading && _regionBins.Count == 0)
        {
            string src = SrcBox.Text.Trim();
            bool valid = src.Length > 0
                && (RegionSourceResolver.IsUrl(src) || File.Exists(src));
            if (valid && !string.Equals(_autoResolvedSource, src, StringComparison.Ordinal))
            {
                _autoResolvedSource = src;
                _ = ResolveSourceAsync();
            }
        }

        UpdateButtons();
    }

    // ── 패키지(zip/폴더) 선택·해석 ───────────────────────────────────────────

    /// <summary>
    /// 지난번 원본이 있던 폴더. 지난번 것이 폴더였어도 그 <b>부모</b>를 연다 —
    /// 새 버전은 대개 옆 폴더로 나오므로 형제 폴더가 보이는 편이 고르기 쉽다.
    /// </summary>
    private string? LastSourceParent()
    {
        try
        {
            return _state.LastFlashZip is { Length: > 0 } last
                   && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(last)) is { Length: > 0 } dir
                   && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.S("Flash.PickTitle"),
            Filter = Loc.S("Flash.PickFilter"),
            CheckFileExists = true,
        };
        if (LastSourceParent() is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;

        LoadPackage(dlg.FileName);
    }

    /// <summary>압축을 푼 폴더나 ESP-IDF build 폴더를 고른다. bin 을 복사하지 않고 그 자리의 파일을 쓴다.</summary>
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.S("Flash.PickFolderTitle") };
        if (LastSourceParent() is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;

        LoadPackage(dlg.FolderName);
    }

    /// <summary><paramref name="sourcePath"/> 는 zip 파일이나 폴더다. 해석 규칙은 둘이 같다.</summary>
    private void LoadPackage(string sourcePath)
    {
        _rows.Clear();
        NoticePane.Visibility = Visibility.Collapsed;
        NoticeText.Text = "";
        ZipBox.Text = sourcePath;

        // 후보별 크기 맵(파일을 바꾸면 크기 표시도 따라가게)
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var src = FlashSources.Open(sourcePath);
            using (src as IDisposable)
            {
                _package = FlashPackageAnalyzer.Analyze(src, sourcePath, SelectedChipOverride());
                foreach (var (name, size) in src.Files) sizes[name] = size;
            }
        }
        catch (Exception ex)
        {
            _package = null;
            AppendNotice(Loc.F("Flash.Msg.OpenFailed", ex.Message));
            UpdateButtons();
            return;
        }

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

        _state.LastFlashZip = sourcePath;
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
        bool packageReady = _package is { Errors.Count: 0 }
                            && _rows.Any(r => r.Selected && r.FileName is not null);
        bool regionReady = !_srcLoading
                           && RegionAddress is not null
                           && SelectedBinPath is { } bin && File.Exists(bin);
        bool onRegionTab = ReferenceEquals(ModeTabs.SelectedItem, RegionTab);

        StartButton.IsEnabled = !_running
                                && _tool is not null
                                && !string.IsNullOrEmpty(_portName)
                                && (onRegionTab ? regionReady : packageReady);
        // 중지는 실행 중에만 나타난다 — 대기 상태에서 비활성 버튼이 자리를 지킬 이유가 없다.
        // 전체 지우기는 패키지와 무관하다 — 도구와 포트만 있으면 어느 탭에서든 누를 수 있다.
        EraseButton.IsEnabled = !_running && _tool is not null && !string.IsNullOrEmpty(_portName);
        StopButton.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = _running;
        CloseButton.IsEnabled = !_running;
        // 진행바도 실행 중에만(대기 중엔 배경과 구분 안 되는 검은 막대일 뿐이다).
        ProgressRow.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        // 목록이 비면 안내를 겹쳐 보인다.
        ItemListEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.IsEnabled = !_running;
        BrowseFolderButton.IsEnabled = !_running;
        ChipBox.IsEnabled = !_running;
        BaudBox.IsEnabled = !_running;
        ItemList.IsEnabled = !_running;
        ModeTabs.IsEnabled = !_running;          // 실행 중 탭 전환 = 다른 모드의 시작 조건으로 바뀌는 혼란
        SrcBox.IsEnabled = !_running && !_srcLoading;
        SrcBrowseButton.IsEnabled = !_running && !_srcLoading;
        SrcLoadButton.IsEnabled = !_running && !_srcLoading;
        AddrBox.IsEnabled = !_running;
        BinBox.IsEnabled = !_running && !_srcLoading;

        // '다음에 눌러야 할 버튼' 을 강조색으로: 소스는 있는데 BIN 이 아직 준비 안 됐으면
        // [불러오기] 가 파란 버튼이 된다 — 시작 버튼이 왜 비활성인지 화면이 스스로 설명하게.
        bool needLoad = !_running && !_srcLoading
                        && SrcBox.Text.Trim().Length > 0 && _regionBins.Count == 0;
        SrcLoadButton.Style = (Style)FindResource(needLoad ? "SendButton" : "DialogButton");

        // BIN 칸 옆 상태 문구: 크기(준비됨) / 불러오는 중 / 불러오기 유도
        if (SelectedBinPath is { } binPath && File.Exists(binPath))
        {
            BinInfoText.Text = $"{new FileInfo(binPath).Length / 1024.0:N1} KB";
            BinInfoText.Foreground = Theme.Brush("TextDim");
        }
        else if (_srcLoading)
        {
            BinInfoText.Text = Loc.S("Flash.RegionLoading");
            BinInfoText.Foreground = Theme.Brush("TextDim");
        }
        else if (SrcBox.Text.Trim().Length > 0)
        {
            BinInfoText.Text = Loc.S("Flash.RegionNeedLoad");
            BinInfoText.Foreground = Theme.Brush("Amber");
        }
        else
        {
            BinInfoText.Text = "";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(ModeTabs.SelectedItem, RegionTab))
        {
            await StartRegionAsync();
            return;
        }

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
        string sourcePath = ZipBox.Text;
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

        await RunEsptoolAsync(reconnectAfter, "Flash.Log.Done", timed: false, async ct =>
        {
            // 파일명 → 실제 경로. 폴더는 그 자리의 파일을 쓰고, zip 은 작업 폴더에 푼다(재사용).
            Func<string, string?> pathOf;
            if (FlashSources.IsFolder(sourcePath))
            {
                Log(Loc.F("Flash.Log.UseFolder", sourcePath));
                var dir = await Task.Run(() => new DirectoryFlashSource(sourcePath), ct);
                pathOf = dir.FullPath;
            }
            else
            {
                string root = Path.Combine(AppState.Dir, "flash", FlashExtractor.WorkFolderName(sourcePath));
                Log(Loc.F("Flash.Log.Extract", root));
                var map = await Task.Run(() => FlashExtractor.Extract(sourcePath, root), ct);
                pathOf = name => map.TryGetValue(name, out string? full) ? full : null;
            }

            var files = new List<(uint Offset, string File)>();
            foreach (var p in plan)
            {
                if (pathOf(p.Name) is not { } full || !File.Exists(full))
                    throw new FileNotFoundException(Loc.F("Flash.Msg.PackageFileMissing", p.Name));
                files.Add((p.Offset, full));
            }

            var request = new FlashRequest
            {
                Port = _portName,
                Baud = baud,
                Chip = chip,
                Files = files,
                KeepFlashSettings = keepSettings,
                FlashMode = _package!.Args.FlashMode,
                FlashFreq = _package.Args.FlashFreq,
                FlashSize = _package.Args.FlashSize,
            };
            // 크기는 표가 아니라 실제 파일에서 다시 잰다 — 폴더는 불러온 뒤 다시 빌드됐을 수 있다.
            return (EsptoolCommand.BuildWriteFlash(request, _tool!.UsesHyphenSyntax),
                    files.Select(f => new FileInfo(f.File).Length).ToList());
        });
    }

    /// <summary>영역 굽기: 선택한 bin 하나를 입력한 주소에 쓴다(칩은 esptool 자동 감지). 실행 경로는 패키지 굽기와 공유.</summary>
    private async Task StartRegionAsync()
    {
        if (_running || _tool is null) return;
        if (RegionAddress is not { } address || SelectedBinPath is not { } bin || !File.Exists(bin)) return;

        long size = new FileInfo(bin).Length;
        bool reconnectAfter = ReconnectBox.IsChecked == true;
        bool keepSettings = KeepBox.IsChecked == true;
        int baud = BaudBox.SelectedItem is int b ? b : 576000;

        // 주소는 사용자가 직접 입력한 값이다 — 잘못 구우면 장비가 손상될 수 있으므로
        // 확인창이 마지막 검증 자리다(안내문의 기본값 0xD90000 과 대조할 수 있게 hex 로 보여 준다).
        var confirm = MessageBox.Show(this,
            Loc.F("Flash.Msg.RegionConfirm", _portName, Path.GetFileName(bin), $"{size / 1024.0:N1}",
                  $"0x{address:X}", baud),
            Loc.S("Flash.Msg.ConfirmTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (confirm != MessageBoxResult.OK) return;

        await RunEsptoolAsync(reconnectAfter, "Flash.Log.Done", timed: false, _ =>
        {
            var request = new FlashRequest
            {
                Port = _portName,
                Baud = baud,
                Chip = EspChip.Unknown,        // --chip 생략 → esptool 이 접속해서 자동 감지
                Files = new[] { (address, bin) },
                KeepFlashSettings = keepSettings,
            };
            return Task.FromResult((EsptoolCommand.BuildWriteFlash(request, _tool!.UsesHyphenSyntax),
                                    new List<long> { size }));
        });
    }

    /// <summary>
    /// 플래시 전체 지우기(erase_flash). 패키지와 무관하므로 칩은 esptool 자동 감지에 맡긴다.
    /// 되돌릴 수 없는 동작이라 확인창의 기본 단추를 [취소]로 둔다.
    /// </summary>
    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _tool is null || string.IsNullOrEmpty(_portName)) return;

        int baud = BaudBox.SelectedItem is int b ? b : 576000;
        bool reconnectAfter = ReconnectBox.IsChecked == true;
        bool hyphen = _tool.UsesHyphenSyntax;

        var confirm = MessageBox.Show(this, Loc.F("Flash.Msg.EraseConfirm", _portName),
            Loc.S("Flash.Msg.EraseTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        await RunEsptoolAsync(reconnectAfter, "Flash.Log.EraseDone", timed: true, _ =>
            Task.FromResult((EsptoolCommand.BuildEraseFlash(_portName, baud, EspChip.Unknown, hyphen),
                             new List<long>())));
    }

    /// <summary>
    /// 공용 실행 경로: 준비(<paramref name="prepare"/>) → 포트 양보 → esptool → 포트 복귀.
    /// 패키지·부분 플래시·전체 지우기가 인자만 다르고 수명주기는 같아야 하므로 한곳에 둔다 —
    /// 복귀(finally)가 여러 벌이면 한쪽만 고쳐지는 날이 온다.
    /// <paramref name="prepare"/> 는 esptool 인자와 파일 크기(진행률 계산용)를 돌려준다.
    /// <paramref name="timed"/> 면 퍼센트 대신 경과 시간을 보인다(바이트 진행률이 없는 작업).
    /// </summary>
    private async Task RunEsptoolAsync(bool reconnectAfter, string doneLogKey, bool timed,
        Func<CancellationToken, Task<(List<string> Args, List<long> Sizes)>> prepare)
    {
        if (_tool is null) return;

        _running = true;
        _cts = new CancellationTokenSource();
        _elapsed = null;   // 지난 작업의 경과 시간이 다음 작업의 퍼센트 자리를 차지하지 않게
        LogBox.Clear();
        Progress.Value = 0;
        SetProgressText("Flash.Phase.Preparing", 0);
        UpdateButtons();

        bool released = false;
        try
        {
            // 1) 준비(압축 해제·인자 구성 — 모드별)
            var (args, sizes) = await prepare(_cts.Token);

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
            Log(EsptoolCommand.ToDisplayLine(_tool.Path, args));
            Log("");

            var runner = new EsptoolRunner(_tool.Path);
            runner.Line += line => Dispatcher.BeginInvoke(() => Log(line));
            runner.Progress += p => Dispatcher.BeginInvoke(() =>
            {
                Progress.Value = p.Fraction;
                SetProgressText(p.Phase, p.Fraction);
            });

            if (timed) StartElapsed();
            var result = await runner.RunAsync(args, sizes, _cts.Token);
            StopElapsed();

            if (result.Ok)
            {
                Progress.Value = 1;
                SetProgressText("Flash.Phase.Done", 1);
                Log("");
                Log(Loc.S(doneLogKey));
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
            StopElapsed();   // 예외로 빠져나와도 타이머가 남지 않게

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
        _phaseKey = phaseKey;
        string tail = _elapsed is { } sw
            ? Loc.F("Flash.Elapsed", (int)sw.Elapsed.TotalSeconds)
            : $"{fraction * 100:0}%";
        ProgressText.Text = $"{Loc.S(phaseKey)}  ·  {tail}";
    }

    /// <summary>경과 시간 표시를 시작한다. 단계 문구는 그대로 두고 초만 1초마다 올린다.</summary>
    private void StartElapsed()
    {
        _elapsed = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => SetProgressText(_phaseKey, 0);
        _elapsedTimer.Start();
        SetProgressText(_phaseKey, 0);
    }

    /// <summary>타이머만 멈춘다. 스톱워치는 남겨 "완료 · 14초" 처럼 마지막 시간을 보여 준다.</summary>
    private void StopElapsed()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
        _elapsed?.Stop();
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
