using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ShaolanPet;

record Clip(int Id, string Label, string File, double Duration, bool DefaultOff);

sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public double Volume { get; set; } = 0.65;
    public int Size { get; set; } = 260;
    public bool RandomMode { get; set; } = true;
    public int? PinnedId { get; set; }
    public List<int>? ExcludedIds { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
}

sealed class ShuffleBag
{
    private readonly List<int> remaining = new();
    private int? last;
    public void Reset() => remaining.Clear();
    public int? Next(IReadOnlyCollection<int> ids)
    {
        remaining.RemoveAll(id => !ids.Contains(id));
        if (ids.Count == 0) return null;
        if (remaining.Count == 0)
        {
            remaining.AddRange(ids);
            for (int i = remaining.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
            }
            if (remaining.Count > 1 && remaining[^1] == last)
                (remaining[0], remaining[^1]) = (remaining[^1], remaining[0]);
        }
        int next = remaining[^1];
        remaining.RemoveAt(remaining.Count - 1);
        return last = next;
    }
}

sealed record PetDefinition(string Id, string Name, string Directory, int ExpectedClips, int DefaultOffset)
{
    public string AssetDirectory => Path.Combine(Program.Assets, Directory);
    public string SettingsPath => Path.Combine(Program.SettingsRoot, Id == "shaolan" ? "settings.json" : Id + ".json");
    public List<Clip> LoadClips()
    {
        var clips = JsonSerializer.Deserialize<List<Clip>>(File.ReadAllText(Path.Combine(AssetDirectory, "clips.json")), Program.JsonOptions)
            ?? throw new InvalidDataException(Name + "语音清单无法读取");
        if (clips.Count != ExpectedClips || clips.Select(c => c.Id).Distinct().Count() != clips.Count ||
            clips.Any(c => c.Duration <= 0 || !File.Exists(Path.Combine(AssetDirectory, "Clips", c.File))))
            throw new InvalidDataException(Name + "语音素材缺失或清单有误");
        return clips;
    }
    public Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Program.JsonOptions) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void SaveSettings(Settings settings)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Program.JsonOptions));
    }
}

static class Program
{
    internal static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    internal static string SettingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaolanPet");
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal static readonly PetDefinition[] Pets = {
        new("shaolan", "薄肌", "", 37, 0),
        new("sunpet", "太对了", "SunPet", 28, 1),
        new("fengge", "峰哥", "FengGe", 11, 2)
    };
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--version"))
            {
                Console.WriteLine("ShaolanPet.Windows " + typeof(Program).Assembly.GetName().Version);
                return 0;
            }
            if (args.Contains("--dump-state"))
            {
                using var source = new CodexMessageWatcher(_ => { });
                source.PollOnce();
                Console.WriteLine(JsonSerializer.Serialize(new {
                    version = typeof(Program).Assembly.GetName().Version?.ToString(),
                    codexReactionEnabled = ControlPanel.LoadPanelSettings().CodexReaction,
                    scope = "selected local Codex Desktop chats only",
                    sourceProbeStatus = source.Status,
                    selectedChatCount = source.SelectedChatCount,
                    watchingChatCount = source.WatchingChatCount,
                    mode = "one-shot metadata probe; running app is not queried"
                }, JsonOptions));
                return 0;
            }
            var clips = Pets.ToDictionary(p => p.Id, p => p.LoadClips());
            if (args.Contains("--self-test")) return SelfTest(clips);
            if (args.Contains("--playback-test")) return PlaybackTest(clips);
            if (args.Contains("--codex-playback-test")) return PlaybackTest(clips, codex: true);
            using var mutex = new System.Threading.Mutex(true, @"Local\ShaolanPet.Windows", out bool first);
            if (!first) return 0;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new ControlPanel(clips));
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Contains("--self-test") || args.Contains("--playback-test") || args.Contains("--codex-playback-test") || args.Contains("--dump-state")) {
                Console.Error.WriteLine("FAIL: " + ex);
                var log = Environment.GetEnvironmentVariable("SHAOLAN_TEST_LOG");
                if (!string.IsNullOrEmpty(log)) File.WriteAllText(log, ex.ToString());
            }
            else MessageBox.Show("桌宠启动失败：" + ex.Message, "桌宠控制面板", MessageBoxButton.OK, MessageBoxImage.Error);
            // Hosted runners without an audio device cannot exercise actual playback.
            if ((args.Contains("--playback-test") || args.Contains("--codex-playback-test")) && HasAudioDeviceError(ex)) return 2;
            return 1;
        }
    }
    private static bool HasAudioDeviceError(Exception? error)
    {
        while (error != null) {
            if (error.HResult == unchecked((int)0xC00D11BA)) return true;
            error = error.InnerException;
        }
        return false;
    }
    // Exercise real MediaPlayer completion events and the full reply chain on Windows.
    static int PlaybackTest(Dictionary<string, List<Clip>> allClips, bool codex = false)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ShaolanPet-playback-test-" + Guid.NewGuid());
        SettingsRoot = testRoot;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var panel = new ControlPanel(allClips, startWatcher: false);
            var heard = new List<string>();
            Exception? failure = null;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(codex ? 60 : 30) };
            timeout.Tick += (_, _) => { timeout.Stop(); failure = new TimeoutException("音频播放或接话超时：" + string.Join(" → ", heard)); panel.Close(); };
            panel.Loaded += (_, _) => {
                try {
                    Action<ConversationTurn> onEnded = turn => {
                        heard.Add(turn.Speaker);
                        if (heard.Count != (codex ? 2 : 4)) return;
                        if (!heard.SequenceEqual(codex ? new[] { "sunpet", "sunpet" } : new[] { "shaolan", "sunpet", "fengge", "sunpet" }))
                            failure = new InvalidDataException("接话顺序错误：" + string.Join(" → ", heard));
                        timeout.Stop(); panel.Dispatcher.BeginInvoke(new Action(panel.Close));
                    };
                    Action<Exception> onFailure = error => {
                        failure = new InvalidDataException("音频播放失败，已完成：" + string.Join(" → ", heard), error);
                        timeout.Stop(); panel.Dispatcher.BeginInvoke(new Action(panel.Close));
                    };
                    if (codex) panel.StartCodexPlaybackTest(onEnded, onFailure);
                    else panel.StartPlaybackTest(onEnded, onFailure);
                    timeout.Start();
                }
                catch (Exception e) { failure = e; panel.Close(); }
            };
            app.Run(panel);
            if (failure != null) throw new InvalidDataException("Windows 播放联动测试失败", failure);
            return 0;
        }
        finally { if (System.IO.Directory.Exists(testRoot)) System.IO.Directory.Delete(testRoot, true); }
    }
    static int SelfTest(Dictionary<string, List<Clip>> allClips)
    {
        foreach (var pet in Pets)
        {
            var image = new BitmapImage(new Uri(Path.Combine(pet.AssetDirectory, "face.png")));
            if (image.PixelWidth == 0 || image.PixelHeight == 0) throw new InvalidDataException(pet.Name + "头像读取失败");
            var clips = allClips[pet.Id];
            foreach (var c in clips)
            {
                using var reader = new BinaryReader(File.OpenRead(Path.Combine(pet.AssetDirectory, "Clips", c.File)));
                if (c.File.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException(c.File);
                    reader.ReadInt32();
                    if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException(c.File);
                }
                else if (c.File.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                {
                    reader.ReadInt32();
                    if (new string(reader.ReadChars(4)) != "ftyp") throw new InvalidDataException(c.File);
                }
                else throw new InvalidDataException("不支持的音频：" + c.File);
            }
            var bag = new ShuffleBag(); int? last = null;
            int[] ids = clips.Select(c => c.Id).ToArray();
            for (int round = 0; round < 100; round++)
            {
                var seen = new HashSet<int>();
                foreach (int _ in ids)
                {
                    int value = bag.Next(ids) ?? throw new InvalidDataException("随机池为空");
                    if ((ids.Length > 1 && value == last) || !seen.Add(value)) throw new InvalidDataException("随机池重复");
                    last = value;
                }
            }
            bag.Reset();
            if (bag.Next(new[] { ids[0] }) != ids[0] || bag.Next(Array.Empty<int>()) != null)
                throw new InvalidDataException("轮播筛选失败");
        }
        ConversationTests.Verify();
        CodexReactionTests.Verify();
        var testRoot = Path.Combine(Path.GetTempPath(), "ShaolanPet-self-test-" + Guid.NewGuid());
        SettingsRoot = testRoot;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var panel = new ControlPanel(allClips, startWatcher: false);
            Exception? failure = null;
            panel.Loaded += (_, _) => {
                try { panel.VerifyControls(); }
                catch (Exception e) { failure = e; }
                finally { panel.Close(); }
            };
            app.Run(panel);
            if (failure != null) throw new InvalidDataException("控制面板自检失败", failure);
        }
        finally { if (System.IO.Directory.Exists(testRoot)) System.IO.Directory.Delete(testRoot, true); }
        return 0;
    }
}

sealed class PetWindow : Window
{
    public static readonly (string Name, int Value)[] SizeOptions = { ("小", 190), ("中", 260), ("大", 340) };
    public static int NormalizeSize(int size) => SizeOptions.Any(option => option.Value == size) ? size : 260;
    private readonly PetDefinition pet;
    private readonly List<Clip> clips;
    private readonly Settings settings;
    private readonly ControlPanel panel;
    private readonly ShuffleBag bag = new();
    private MediaPlayer? player;
    private ConversationTurn? playingTurn;
    private bool playing;
    private readonly ShuffleBag replyBag = new();
    private readonly ShuffleBag codexBag = new();
    private readonly Image face = new();
    private readonly TextBlock caption = new();
    private readonly DispatcherTimer captionTimer = new();
    private readonly ScaleTransform faceScale = new(1, 1);
    private Point mouseDown;
    private bool dragging;
    private int? lastPlayed;

    public PetWindow(PetDefinition pet, List<Clip> clips, Settings settings, ControlPanel panel)
    {
        this.pet = pet; this.clips = clips; this.settings = settings; this.panel = panel;
        settings.ExcludedIds ??= clips.Where(c => c.DefaultOff).Select(c => c.Id).ToList();
        settings.ExcludedIds = settings.ExcludedIds.Intersect(clips.Select(c => c.Id)).ToList();
        if (settings.ExcludedIds.Count == clips.Count) settings.ExcludedIds.Clear();
        if (!clips.Any(c => c.Id == settings.PinnedId)) settings.RandomMode = true;
        settings.Size = NormalizeSize(settings.Size);
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        Title = pet.Name + "桌宠"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Manual;
        Width = settings.Size; Height = settings.Size + 44;
        var grid = new Grid { Background = Brushes.Transparent, ToolTip = "点击播放 · 拖动移动 · 右键设置" };
        face.Source = new BitmapImage(new Uri(Path.Combine(pet.AssetDirectory, "face.png")));
        face.Stretch = Stretch.Uniform; face.Margin = new Thickness(8, 44, 8, 8);
        face.RenderTransformOrigin = new Point(.5, 1); face.RenderTransform = faceScale;
        grid.Children.Add(face);
        var bubble = new Border { Background = new SolidColorBrush(Color.FromArgb(195, 0, 0, 0)), CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 3, 3, 0), Padding = new Thickness(6), Child = caption };
        caption.Foreground = Brushes.White; caption.TextAlignment = TextAlignment.Center; caption.TextWrapping = TextWrapping.Wrap;
        bubble.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = caption });
        grid.Children.Add(bubble); Content = grid;
        captionTimer.Tick += (_, _) => { captionTimer.Stop(); caption.Visibility = Visibility.Collapsed; };
        grid.MouseLeftButtonDown += (_, e) => { mouseDown = e.GetPosition(this); dragging = false; grid.CaptureMouse(); };
        grid.MouseMove += (_, e) => {
            if (e.LeftButton != MouseButtonState.Pressed || !grid.IsMouseCaptured) return;
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - mouseDown.X) + Math.Abs(p.Y - mouseDown.Y) < 5) return;
            dragging = true; grid.ReleaseMouseCapture();
            try { DragMove(); } catch (InvalidOperationException) { }
            SavePosition();
        };
        grid.MouseLeftButtonUp += (_, _) => { if (grid.IsMouseCaptured) grid.ReleaseMouseCapture(); if (!dragging) PlayNext(); };
        grid.MouseRightButtonUp += (_, e) => { grid.ContextMenu = MakeMenu(); grid.ContextMenu.IsOpen = true; e.Handled = true; };
        Loaded += (_, _) => { RestorePosition(); ShowCaption("点我说一句 · 右键设置", 5); };
        Closed += (_, _) => { StopPlayback(); SavePosition(); panel.PetClosed(pet.Id); };
    }
    private void Save()
    {
        try { pet.SaveSettings(settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowCaption("设置保存失败", 3); }
    }
    public void SetVolume(double volume) { settings.Volume = Math.Clamp(volume, 0, 1); if (player != null) player.Volume = settings.Volume; }
    public void SetSize(int size)
    {
        settings.Size = NormalizeSize(size);
        Width = settings.Size; Height = settings.Size + 44;
        SavePosition(); panel.UpdateSize(pet.Id);
    }
    public bool IsPlaying => playing;
    public bool IsCodexReaction => playing && playingTurn?.Root == "codex";
    internal int? LastPlayedId => lastPlayed;
    public void StopPlayback()
    {
        if (playingTurn is { } turn) panel.CancelIfCurrent(turn);
        playingTurn = null; playing = false;
        player?.Stop(); player?.Close(); player = null;
        Animate(false); captionTimer.Stop(); caption.Visibility = Visibility.Collapsed;
    }
    private void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        if (settings.Left is double x && settings.Top is double y && x + Width > area.Left + 80 && x < area.Right - 80 && y + Height > area.Top + 80 && y < area.Bottom - 80) { Left = x; Top = y; }
        else ResetPosition();
    }
    private void ResetPosition() { var area = SystemParameters.WorkArea; Left = Math.Max(area.Left, area.Right - Width - 32 - pet.DefaultOffset * (Width + 12)); Top = Math.Max(area.Top, area.Bottom - Height - 24); }
    private void SavePosition() { settings.Left = Left; settings.Top = Top; Save(); }
    private void ShowCaption(string text, double seconds)
    {
        caption.Text = text; caption.Visibility = Visibility.Visible; captionTimer.Stop(); captionTimer.Interval = TimeSpan.FromSeconds(seconds); captionTimer.Start();
    }
    private void Animate(bool enabled)
    {
        faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        faceScale.ScaleY = 1;
        if (enabled && SystemParameters.ClientAreaAnimation)
            faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, .88, TimeSpan.FromMilliseconds(160)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void PlayNext()
    {
        int? id = settings.RandomMode ? bag.Next(clips.Where(c => !settings.ExcludedIds!.Contains(c.Id)).Select(c => c.Id).ToArray()) : settings.PinnedId;
        var clip = clips.FirstOrDefault(c => c.Id == id);
        if (clip != null) Play(clip);
    }
    public void PlayFirstForTest() => Play(clips[0]);
    private void Play(Clip clip)
    {
        var turn = panel.StartConversation(pet.Id);
        StartPlayback(clip, turn);
    }
    public void CompletePlaybackForTest()
    {
        if (!panel.SimulatedPlayback) throw new InvalidOperationException("仅供自检");
        if (playingTurn is { } turn) FinishPlayback(turn);
    }
    private void FinishPlayback(ConversationTurn turn)
    {
        if (playingTurn != turn) return;
        playing = false; playingTurn = null; Animate(false);
        player?.Close(); player = null;
        panel.ConversationEnded(turn);
    }
    public bool TryPlayCodexReaction()
    {
        if (pet.Id != "sunpet" || playing) return false;
        var available = clips.Where(c => !settings.ExcludedIds!.Contains(c.Id)).Select(c => c.Id).ToArray();
        if (available.Length > 1) available = available.Where(id => id != lastPlayed).ToArray();
        var id = codexBag.Next(available);
        var clip = clips.FirstOrDefault(c => c.Id == id);
        return clip != null && StartPlayback(clip, panel.StartCodexReaction());
    }
    public bool PlayReply(ConversationTurn turn)
    {
        // SunPet's original short response pool; respect the user's rotation exclusions.
        int[] shortReplies = { 1, 2, 4, 11, 13, 14 };
        var available = clips.Where(c => !settings.ExcludedIds!.Contains(c.Id) &&
            (pet.Id != "sunpet" || shortReplies.Contains(c.Id))).Select(c => c.Id).ToArray();
        var id = replyBag.Next(available);
        var clip = clips.FirstOrDefault(c => c.Id == id);
        if (clip == null || playing) return false;
        return StartPlayback(clip, turn);
    }
    private bool StartPlayback(Clip clip, ConversationTurn turn)
    {
        StopPlayback();
        try
        {
            if (panel.SimulatedPlayback) { playingTurn = turn; playing = true; lastPlayed = clip.Id; return true; }
            var nextPlayer = new MediaPlayer();
            player = nextPlayer; playingTurn = turn; playing = true;
            nextPlayer.MediaEnded += (_, _) => {
                if (!ReferenceEquals(player, nextPlayer) || playingTurn != turn) return;
                FinishPlayback(turn);
            };
            nextPlayer.MediaFailed += (_, error) => {
                if (!ReferenceEquals(player, nextPlayer) || playingTurn != turn) return;
                playing = false; playingTurn = null; Animate(false);
                nextPlayer.Close(); player = null;
                panel.CancelIfCurrent(turn); panel.NotifyPlaybackFailed(error.ErrorException); ShowCaption("音频读取失败", 4);
            };
            nextPlayer.Open(new Uri(Path.Combine(pet.AssetDirectory, "Clips", clip.File)));
            nextPlayer.Volume = settings.Volume; nextPlayer.Play();
            lastPlayed = clip.Id; Animate(true); ShowCaption(clip.Label, clip.Duration + .4);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException)
        {
            playing = false; playingTurn = null; player?.Close(); player = null;
            panel.CancelIfCurrent(turn); Animate(false); ShowCaption("音频读取失败", 4); return false;
        }
    }
    private MenuItem Item(string title, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = title, IsChecked = isChecked };
        item.Click += (_, _) => action(); return item;
    }
    private ContextMenu MakeMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{pet.Name}桌宠 · {clips.Count}句原声", IsEnabled = false });
        menu.Items.Add(Item("随机说一句", PlayNext));
        var choose = new MenuItem { Header = "挑一句" };
        foreach (var clip in clips) choose.Items.Add(Item($"{clip.Id:00}  {clip.Label}", () => { if (!settings.RandomMode) { settings.PinnedId = clip.Id; Save(); } Play(clip); }));
        menu.Items.Add(choose);
        var mode = new MenuItem { Header = "播放方式" };
        mode.Items.Add(Item("随机一句（一轮不重复）", () => { settings.RandomMode = true; Save(); ShowCaption("随机播放 · 一轮不重复", 3); }, settings.RandomMode));
        mode.Items.Add(Item("固定播一句", () => { settings.RandomMode = false; settings.PinnedId ??= lastPlayed ?? clips[0].Id; Save(); ShowCaption("已固定：" + clips.First(c => c.Id == settings.PinnedId).Label, 3); }, !settings.RandomMode));
        menu.Items.Add(mode);
        var filters = new MenuItem { Header = "轮播内容" };
        foreach (var clip in clips)
        {
            var entry = new MenuItem { Header = $"{clip.Id:00}  {clip.Label}", IsCheckable = true, IsChecked = !settings.ExcludedIds!.Contains(clip.Id), StaysOpenOnClick = true };
            entry.Click += (_, _) => {
                if (entry.IsChecked) settings.ExcludedIds!.Remove(clip.Id);
                else if (settings.ExcludedIds!.Count < clips.Count - 1) settings.ExcludedIds.Add(clip.Id);
                else { entry.IsChecked = true; ShowCaption("至少留一句在轮播里", 3); return; }
                bag.Reset(); Save();
            };
            filters.Items.Add(entry);
        }
        filters.Items.Add(new Separator());
        filters.Items.Add(Item("全部恢复", () => { settings.ExcludedIds!.Clear(); bag.Reset(); Save(); })); menu.Items.Add(filters);
        menu.Items.Add(Item("停止播放", StopPlayback));
        var volume = new MenuItem { Header = "音量" };
        foreach (int value in new[] { 0, 25, 50, 65, 80, 100 }) volume.Items.Add(Item(value == 0 ? "静音" : $"{value}%", () => { SetVolume(value / 100.0); Save(); panel.UpdateVolume(pet.Id); }, Math.Abs(settings.Volume - value / 100.0) < .01));
        menu.Items.Add(volume);
        var size = new MenuItem { Header = "人物大小" };
        foreach (var (name, value) in SizeOptions) size.Items.Add(Item(name, () => SetSize(value), settings.Size == value));
        menu.Items.Add(size);
        menu.Items.Add(Item("移回屏幕右下角", () => { ResetPosition(); SavePosition(); }));
        menu.Items.Add(Item("打开控制面板", panel.ShowPanel));
        menu.Items.Add(new Separator()); menu.Items.Add(Item("关闭这个桌宠", Close));
        return menu;
    }
}
