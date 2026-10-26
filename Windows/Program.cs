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

static class Program
{
    internal static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var clips = JsonSerializer.Deserialize<List<Clip>>(File.ReadAllText(Path.Combine(Assets, "clips.json")), JsonOptions)
                ?? throw new InvalidDataException("无法读取语音清单");
            if (clips.Count == 0 || clips.Select(c => c.Id).Distinct().Count() != clips.Count ||
                clips.Any(c => !File.Exists(Path.Combine(Assets, "Clips", c.File))))
                throw new InvalidDataException("语音素材缺失或清单有误");
            if (args.Contains("--self-test")) return SelfTest(clips);
            using var mutex = new System.Threading.Mutex(true, @"Local\ShaolanPet.Windows", out bool first);
            if (!first) return 0;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new PetWindow(clips));
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Contains("--self-test")) Console.Error.WriteLine("FAIL: " + ex);
            else MessageBox.Show("桌宠启动失败：" + ex.Message, "薄肌桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    static int SelfTest(List<Clip> clips)
    {
        // Decode every packaged asset and exercise shuffle boundaries without touching user settings.
        var image = new BitmapImage(new Uri(Path.Combine(Assets, "face.png")));
        if (image.PixelWidth == 0 || image.PixelHeight == 0) throw new InvalidDataException("头像读取失败");
        foreach (var c in clips)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(Assets, "Clips", c.File)));
            if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException(c.File);
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException(c.File);
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
        return 0;
    }
}

sealed class PetWindow : Window
{
    private readonly List<Clip> clips;
    private readonly Settings settings;
    private readonly ShuffleBag bag = new();
    private readonly MediaPlayer player = new();
    private readonly Image face = new();
    private readonly TextBlock caption = new();
    private readonly DispatcherTimer captionTimer = new();
    private readonly ScaleTransform faceScale = new(1, 1);
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaolanPet", "settings.json");
    private Point mouseDown;
    private bool dragging;
    private int? lastPlayed;

    public PetWindow(List<Clip> clips)
    {
        this.clips = clips;
        settings = LoadSettings();
        settings.ExcludedIds ??= clips.Where(c => c.DefaultOff).Select(c => c.Id).ToList();
        settings.ExcludedIds = settings.ExcludedIds.Intersect(clips.Select(c => c.Id)).ToList();
        if (settings.ExcludedIds.Count == clips.Count) settings.ExcludedIds.Clear();
        if (!clips.Any(c => c.Id == settings.PinnedId)) settings.RandomMode = true;
        settings.Size = new[] { 190, 260, 340 }.Contains(settings.Size) ? settings.Size : 260;
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        Title = "薄肌桌宠"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Manual;
        Width = settings.Size; Height = settings.Size + 44;
        var grid = new Grid { Background = Brushes.Transparent, ToolTip = "点击播放 · 拖动移动 · 右键设置" };
        face.Source = new BitmapImage(new Uri(Path.Combine(Program.Assets, "face.png")));
        face.Stretch = Stretch.Uniform; face.Margin = new Thickness(8, 44, 8, 8);
        face.RenderTransformOrigin = new Point(.5, 1); face.RenderTransform = faceScale;
        grid.Children.Add(face);
        var bubble = new Border { Background = new SolidColorBrush(Color.FromArgb(195, 0, 0, 0)), CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 3, 3, 0), Padding = new Thickness(6), Child = caption };
        caption.Foreground = Brushes.White; caption.TextAlignment = TextAlignment.Center; caption.TextWrapping = TextWrapping.Wrap;
        bubble.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = caption });
        grid.Children.Add(bubble); Content = grid;
        captionTimer.Tick += (_, _) => { captionTimer.Stop(); caption.Visibility = Visibility.Collapsed; };
        player.MediaEnded += (_, _) => Animate(false);
        player.MediaFailed += (_, _) => { Animate(false); ShowCaption("音频读取失败", 4); };
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
        Closed += (_, _) => { SavePosition(); player.Close(); };
    }
    private Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath), Program.JsonOptions) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, Program.JsonOptions)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowCaption("设置保存失败", 3); }
    }
    private void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        if (settings.Left is double x && settings.Top is double y && x + Width > area.Left + 80 && x < area.Right - 80 && y + Height > area.Top + 80 && y < area.Bottom - 80) { Left = x; Top = y; }
        else ResetPosition();
    }
    private void ResetPosition() { var area = SystemParameters.WorkArea; Left = area.Right - Width - 32; Top = area.Bottom - Height - 24; }
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
    private void Play(Clip clip)
    {
        player.Stop(); player.Close(); Animate(false);
        player.Open(new Uri(Path.Combine(Program.Assets, "Clips", clip.File))); player.Volume = settings.Volume; player.Play();
        lastPlayed = clip.Id; Animate(true); ShowCaption(clip.Label, clip.Duration + .4);
    }
    private MenuItem Item(string title, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = title, IsChecked = isChecked };
        item.Click += (_, _) => action(); return item;
    }
    private ContextMenu MakeMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"薄肌桌宠 · {clips.Count}句原声", IsEnabled = false });
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
        menu.Items.Add(Item("停止播放", () => { player.Stop(); Animate(false); captionTimer.Stop(); caption.Visibility = Visibility.Collapsed; }));
        var volume = new MenuItem { Header = "音量" };
        foreach (int value in new[] { 0, 25, 50, 65, 80, 100 }) volume.Items.Add(Item(value == 0 ? "静音" : $"{value}%", () => { settings.Volume = value / 100.0; player.Volume = settings.Volume; Save(); }, Math.Abs(settings.Volume - value / 100.0) < .01));
        menu.Items.Add(volume);
        var size = new MenuItem { Header = "人物大小" };
        foreach (var (name, value) in new[] { ("小", 190), ("中", 260), ("大", 340) }) size.Items.Add(Item(name, () => { settings.Size = value; Width = value; Height = value + 44; SavePosition(); }, settings.Size == value));
        menu.Items.Add(size);
        menu.Items.Add(Item("移回屏幕右下角", () => { ResetPosition(); SavePosition(); }));
        menu.Items.Add(new Separator()); menu.Items.Add(Item("退出桌宠", Close));
        return menu;
    }
}
