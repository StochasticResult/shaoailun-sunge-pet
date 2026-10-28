using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShaolanPet;

sealed class PanelSettings
{
    public bool AutoReply { get; set; } = true;
    public bool CodexReaction { get; set; } = true;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; set; }
}

sealed class ControlPanel : Window
{
    private readonly Dictionary<string, List<Clip>> clips;
    private readonly Dictionary<string, Settings> settings = new();
    private readonly Dictionary<string, PetWindow> windows = new();
    private readonly Dictionary<string, CheckBox> switches = new();
    private readonly Dictionary<string, Slider> sliders = new();
    private readonly Dictionary<string, ComboBox> sizes = new();
    private readonly Dictionary<string, TextBlock> values = new();
    private readonly PanelSettings panelSettings;
    private readonly Conversation conversation;
    private readonly CheckBox autoReply;
    private readonly CheckBox codexReaction;
    private readonly TextBlock codexStatus = new() { Foreground = Brushes.DimGray, Margin = new Thickness(22, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly CodexReactionQueue codexQueue;
    private readonly DispatcherTimer codexTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CodexMessageWatcher? codexWatcher;
    private readonly bool startCodexWatcherAllowed;
    private long watcherGeneration;
    private static string PanelSettingsPath => Path.Combine(Program.SettingsRoot, "panel.json");
    public bool SimulatedPlayback { get; private set; }
    private bool shuttingDown;
    private bool updating;

    public ControlPanel(Dictionary<string, List<Clip>> clips, bool startWatcher = true)
    {
        this.clips = clips;
        startCodexWatcherAllowed = startWatcher;
        panelSettings = LoadPanelSettings();
        conversation = new Conversation(panelSettings.AutoReply,
            id => windows.TryGetValue(id, out var window) && !window.IsPlaying,
            turn => windows.TryGetValue(turn.Speaker, out var window) && window.PlayReply(turn));
        codexQueue = new CodexReactionQueue(panelSettings.CodexReaction,
            () => windows.ContainsKey("sunpet") && !windows.Values.Any(w => w.IsPlaying),
            () => windows.TryGetValue("sunpet", out var window) && window.TryPlayCodexReaction());
        codexTimer.Tick += (_, _) => { codexQueue.Drain(); UpdateCodexStatus(); };
        Title = "桌宠控制面板";
        Width = 460; Height = 480; MinWidth = 420; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(248, 249, 252));
        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        page.Children.Add(new TextBlock { Text = "桌宠控制面板", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black });
        page.Children.Add(new TextBlock { Text = "选择要显示的桌宠，分别调整大小和声音。", Margin = new Thickness(0, 5, 0, 16), Foreground = Brushes.DimGray });
        foreach (var pet in Program.Pets)
        {
            var current = pet.LoadSettings();
            current.Volume = Math.Clamp(double.IsFinite(current.Volume) ? current.Volume : .65, 0, 1);
            current.Size = PetWindow.NormalizeSize(current.Size);
            settings.Add(pet.Id, current);
            var card = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)), BorderThickness = new Thickness(1) };
            var content = new StackPanel();
            var on = new CheckBox { Content = pet.Name + "桌宠", IsChecked = current.Enabled, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center };
            switches.Add(pet.Id, on);
            on.Checked += (_, _) => { if (!updating) SetEnabled(pet, true); };
            on.Unchecked += (_, _) => { if (!updating) SetEnabled(pet, false); };
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sizeLabel = new TextBlock { Text = "大小", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var size = new ComboBox { Width = 72, SelectedValuePath = "Tag", VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{pet.Name}桌宠大小" };
            foreach (var (name, sizeValue) in PetWindow.SizeOptions)
                size.Items.Add(new ComboBoxItem { Content = name, Tag = sizeValue });
            size.SelectedValue = current.Size;
            sizes.Add(pet.Id, size);
            size.SelectionChanged += (_, _) => {
                if (updating || size.SelectedValue is not int selected) return;
                if (windows.TryGetValue(pet.Id, out var window)) window.SetSize(selected);
                else { current.Size = selected; Save(pet); }
            };
            Grid.SetColumn(on, 0); Grid.SetColumn(sizeLabel, 1); Grid.SetColumn(size, 2);
            titleRow.Children.Add(on); titleRow.Children.Add(sizeLabel); titleRow.Children.Add(size);
            content.Children.Add(titleRow);
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var label = new TextBlock { Text = "音量", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = current.Volume * 100, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Text = $"{Math.Round(current.Volume * 100)}%", VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            sliders.Add(pet.Id, slider); values.Add(pet.Id, value);
            slider.ValueChanged += (_, _) => {
                if (updating) return;
                current.Volume = slider.Value / 100;
                value.Text = $"{slider.Value:0}%";
                if (windows.TryGetValue(pet.Id, out var window)) window.SetVolume(current.Volume);
                Save(pet);
            };
            Grid.SetColumn(label, 0); Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2);
            row.Children.Add(label); row.Children.Add(slider); row.Children.Add(value); content.Children.Add(row);
            card.Child = content; page.Children.Add(card);
        }
        autoReply = new CheckBox { Content = "自动接话", IsChecked = panelSettings.AutoReply, Margin = new Thickness(2, 4, 0, 6), FontSize = 14 };
        autoReply.Checked += (_, _) => SetAutoReply(true);
        autoReply.Unchecked += (_, _) => SetAutoReply(false);
        page.Children.Add(autoReply);
        codexReaction = new CheckBox { Content = "Codex 消息联动（所选聊天）", IsChecked = panelSettings.CodexReaction, Margin = new Thickness(2, 4, 0, 6), FontSize = 14, ToolTip = "收到所选本机聊天的新用户消息时，太对了随机回应" };
        codexReaction.Checked += (_, _) => SetCodexReaction(true);
        codexReaction.Unchecked += (_, _) => SetCodexReaction(false);
        page.Children.Add(codexReaction);
        page.Children.Add(codexStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var stop = new Button { Content = "停止所有声音", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 0) };
        stop.Click += (_, _) => StopAllPlayback();
        var quit = new Button { Content = "退出全部", Padding = new Thickness(12, 5, 12, 5) };
        quit.Click += (_, _) => Close();
        buttons.Children.Add(stop); buttons.Children.Add(quit); page.Children.Add(buttons);
        page.Children.Add(new TextBlock { Text = "点击人物播放，拖动移动；右键可选择句子和大小。", Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => {
            foreach (var pet in Program.Pets) if (settings[pet.Id].Enabled) OpenPet(pet);
            codexTimer.Start();
            if (startWatcher && panelSettings.CodexReaction) StartCodexWatcher();
        };
        Closing += (_, _) => { shuttingDown = true; codexTimer.Stop(); StopCodexWatcher(); StopAllPlayback(); };
    }

    internal static PanelSettings LoadPanelSettings()
    {
        try { return JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(PanelSettingsPath), Program.JsonOptions) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void SetAutoReply(bool enabled)
    {
        panelSettings.AutoReply = enabled;
        conversation.SetEnabled(enabled);
        SavePanelSettings();
    }
    private void UpdateCodexStatus()
    {
        codexStatus.Text = !panelSettings.CodexReaction ? "消息联动已关闭" : codexWatcher?.Status switch {
            "watching-selected-chat" => "监听所选本机聊天；太对了空闲时回应",
            "source-not-configured" or "source-not-found" => "未找到所选聊天的监听配置",
            "not-started" or null => "等待消息来源",
            _ => "消息来源暂不可用；桌宠仍可正常使用"
        };
    }
    private void SavePanelSettings()
    {
        try {
            Directory.CreateDirectory(Program.SettingsRoot);
            File.WriteAllText(PanelSettingsPath, JsonSerializer.Serialize(panelSettings, Program.JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { MessageBox.Show("设置无法保存：" + e.Message, "桌宠控制面板", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void SetCodexReaction(bool enabled)
    {
        panelSettings.CodexReaction = enabled;
        codexQueue.SetEnabled(enabled);
        if (!enabled && windows.TryGetValue("sunpet", out var sun) && sun.IsCodexReaction) sun.StopPlayback();
        if (enabled && IsLoaded && startCodexWatcherAllowed) StartCodexWatcher();
        else if (!enabled) StopCodexWatcher();
        SavePanelSettings();
        UpdateCodexStatus();
    }
    private void StartCodexWatcher()
    {
        if (codexWatcher != null || shuttingDown) return;
        long generation = ++watcherGeneration;
        try {
            codexWatcher = new CodexMessageWatcher(id => {
                if (Dispatcher.HasShutdownStarted) return;
                try { Dispatcher.BeginInvoke(new Action(() => { if (generation == watcherGeneration) EnqueueCodexReaction(id); })); }
                catch (InvalidOperationException) { }
            });
            codexWatcher.Start();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { StopCodexWatcher(); System.Diagnostics.Trace.WriteLine("Codex reaction source unavailable: " + error.GetType().Name); }
    }
    private void StopCodexWatcher()
    {
        var watcher = codexWatcher; codexWatcher = null;
        watcherGeneration++;
        try { watcher?.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { System.Diagnostics.Trace.WriteLine("Codex reaction source shutdown: " + error.GetType().Name); }
    }
    public void EnqueueCodexReaction(string eventId)
    {
        if (shuttingDown || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => EnqueueCodexReaction(eventId))); return; }
        if (windows.ContainsKey("sunpet")) codexQueue.Enqueue(eventId);
    }
    private void StopAllPlayback()
    {
        codexQueue.Clear(); conversation.Cancel();
        foreach (var window in windows.Values) window.StopPlayback();
    }
    public ConversationTurn StartConversation(string id)
    {
        if (windows.TryGetValue("sunpet", out var sun) && sun.IsCodexReaction) sun.StopPlayback();
        return conversation.Start(id);
    }
    public ConversationTurn StartCodexReaction() => conversation.Start("sunpet", allowReplies: false);
    private Action<ConversationTurn>? playbackTestObserver;
    private Action<Exception>? playbackTestFailure;
    public void ConversationEnded(ConversationTurn turn)
    {
        conversation.Ended(turn);
        playbackTestObserver?.Invoke(turn);
    }
    public void NotifyPlaybackFailed(Exception error) => playbackTestFailure?.Invoke(error);
    public void StartPlaybackTest(Action<ConversationTurn> onEnded, Action<Exception> onFailure)
    {
        foreach (var window in windows.Values) window.SetVolume(0);
        playbackTestObserver = onEnded; playbackTestFailure = onFailure;
        windows["shaolan"].PlayFirstForTest();
    }
    public void StartCodexPlaybackTest(Action<ConversationTurn> onEnded, Action<Exception> onFailure)
    {
        foreach (var window in windows.Values) window.SetVolume(0);
        playbackTestObserver = onEnded; playbackTestFailure = onFailure;
        EnqueueCodexReaction("audio-test-1"); EnqueueCodexReaction("audio-test-2");
    }
    public void CancelIfCurrent(ConversationTurn turn) => conversation.CancelIfCurrent(turn);

    private void Save(PetDefinition pet)
    {
        try { pet.SaveSettings(settings[pet.Id]); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { MessageBox.Show("设置无法保存：" + e.Message, "桌宠控制面板", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void OpenPet(PetDefinition pet)
    {
        if (windows.ContainsKey(pet.Id)) return;
        var window = new PetWindow(pet, clips[pet.Id], settings[pet.Id], this);
        windows.Add(pet.Id, window);
        window.Show();
    }
    private void SetEnabled(PetDefinition pet, bool enabled)
    {
        settings[pet.Id].Enabled = enabled;
        if (enabled) OpenPet(pet);
        else if (windows.TryGetValue(pet.Id, out var window)) window.Close();
        Save(pet);
    }
    public void PetClosed(string id)
    {
        windows.Remove(id);
        conversation.Cancel();
        if (id == "sunpet") codexQueue.Clear();
        if (shuttingDown) return;
        settings[id].Enabled = false;
        updating = true; switches[id].IsChecked = false; updating = false;
        Save(Program.Pets.First(p => p.Id == id));
    }
    public void UpdateVolume(string id)
    {
        updating = true;
        sliders[id].Value = settings[id].Volume * 100;
        values[id].Text = $"{settings[id].Volume * 100:0}%";
        updating = false;
    }
    public void UpdateSize(string id)
    {
        updating = true;
        sizes[id].SelectedValue = settings[id].Size;
        updating = false;
    }
    // Called by --self-test with an isolated settings directory on a Windows runner.
    public void VerifyControls()
    {
        if (windows.Count != Program.Pets.Length) throw new InvalidDataException("桌宠未全部启动");
        if (!JsonSerializer.Deserialize<PanelSettings>("{\"AutoReply\":false}", Program.JsonOptions)!.CodexReaction)
            throw new InvalidDataException("旧设置未默认开启 Codex 反应");
        var previous = JsonSerializer.Deserialize<PanelSettings>("{\"AutoReply\":false,\"ExistingSetting\":123}", Program.JsonOptions)!;
        if (JsonDocument.Parse(JsonSerializer.Serialize(previous, Program.JsonOptions)).RootElement.GetProperty("ExistingSetting").GetInt32() != 123)
            throw new InvalidDataException("面板未知设置未保留");
        codexReaction.IsChecked = false;
        if (LoadPanelSettings().CodexReaction) throw new InvalidDataException("Codex 开关未保存");
        codexReaction.IsChecked = true;
        if (!LoadPanelSettings().CodexReaction) throw new InvalidDataException("Codex 开关未恢复");
        autoReply.IsChecked = false;
        if (LoadPanelSettings().AutoReply) throw new InvalidDataException("联动开关未保存");
        autoReply.IsChecked = true;
        if (!LoadPanelSettings().AutoReply) throw new InvalidDataException("联动开关未恢复");
        VerifyPlaybackFlow();
        VerifyCodexFlow();
        foreach (var pet in Program.Pets)
        {
            sizes[pet.Id].SelectedValue = 340;
            if (windows[pet.Id].Width != 340 || pet.LoadSettings().Size != 340)
                throw new InvalidDataException(pet.Name + "面板大小未生效或未保存");
            windows[pet.Id].SetSize(260);
            if (!Equals(sizes[pet.Id].SelectedValue, 260))
                throw new InvalidDataException(pet.Name + "右键大小未同步面板");
            var area = SystemParameters.WorkArea;
            windows[pet.Id].Left = area.Left + 100;
            windows[pet.Id].Top = area.Top + 100;
            switches[pet.Id].IsChecked = false;
            if (windows.ContainsKey(pet.Id) || settings[pet.Id].Enabled || pet.LoadSettings().Enabled)
                throw new InvalidDataException(pet.Name + "关闭失败");
            if (pet.LoadSettings().Left != area.Left + 100 || pet.LoadSettings().Top != area.Top + 100)
                throw new InvalidDataException(pet.Name + "位置未保存");
            sizes[pet.Id].SelectedValue = 190;
            if (pet.LoadSettings().Size != 190) throw new InvalidDataException(pet.Name + "关闭时大小未保存");
            sliders[pet.Id].Value = 25;
            if (Math.Abs(pet.LoadSettings().Volume - .25) > .001)
                throw new InvalidDataException(pet.Name + "音量未保存");
            switches[pet.Id].IsChecked = true;
            if (!windows.ContainsKey(pet.Id) || !pet.LoadSettings().Enabled)
                throw new InvalidDataException(pet.Name + "重新开启失败");
            if (windows[pet.Id].Width != 190) throw new InvalidDataException(pet.Name + "重新开启时大小不对");
        }
        windows[Program.Pets[0].Id].Close();
        if (switches[Program.Pets[0].Id].IsChecked != false)
            throw new InvalidDataException("关闭桌宠未同步面板");
    }
    private void VerifyPlaybackFlow()
    {
        SimulatedPlayback = true;
        try
        {
            var shao = windows["shaolan"]; var sun = windows["sunpet"]; var feng = windows["fengge"];
            shao.PlayFirstForTest();
            if (!shao.IsPlaying) throw new InvalidDataException("薄肌未开始");
            shao.CompletePlaybackForTest();
            if (!sun.IsPlaying) throw new InvalidDataException("太对了没有接话");
            sun.CompletePlaybackForTest();
            if (!feng.IsPlaying) throw new InvalidDataException("峰哥没有接话");
            feng.CompletePlaybackForTest();
            if (!sun.IsPlaying) throw new InvalidDataException("太对了没有再次接话");
            sun.CompletePlaybackForTest();
            if (windows.Values.Any(w => w.IsPlaying)) throw new InvalidDataException("接话没有结束");
            shao.PlayFirstForTest(); shao.StopPlayback(); shao.CompletePlaybackForTest();
            if (sun.IsPlaying) throw new InvalidDataException("停止后仍接话");
            shao.PlayFirstForTest(); autoReply.IsChecked = false; shao.CompletePlaybackForTest();
            if (sun.IsPlaying) throw new InvalidDataException("关闭联动后仍接话");
            autoReply.IsChecked = true;
            shao.PlayFirstForTest(); switches["sunpet"].IsChecked = false; shao.CompletePlaybackForTest();
            switches["sunpet"].IsChecked = true; sun = windows["sunpet"];
            if (sun.IsPlaying) throw new InvalidDataException("关闭角色后仍接话");
            shao.PlayFirstForTest(); feng.PlayFirstForTest(); shao.CompletePlaybackForTest();
            if (sun.IsPlaying) throw new InvalidDataException("旧播放触发接话");
            feng.CompletePlaybackForTest();
            if (!sun.IsPlaying) throw new InvalidDataException("新播放未接话");
            sun.CompletePlaybackForTest();
        }
        finally { SimulatedPlayback = false; }
    }
    private void VerifyCodexFlow()
    {
        SimulatedPlayback = true;
        var sun = windows["sunpet"]; var shao = windows["shaolan"]; var feng = windows["fengge"];
        var sunSettings = settings["sunpet"];
        bool wasRandom = sunSettings.RandomMode; int? wasPinned = sunSettings.PinnedId;
        try
        {
            StopAllPlayback();
            sunSettings.RandomMode = false; sunSettings.PinnedId = clips["sunpet"][0].Id;
            int? last = null;
            for (int i = 0; i < 60; i++)
            {
                EnqueueCodexReaction("sim-" + i); EnqueueCodexReaction("sim-" + i); codexQueue.Drain();
                if (!sun.IsCodexReaction || shao.IsPlaying || feng.IsPlaying || sun.LastPlayedId == last)
                    throw new InvalidDataException("Codex 未独立随机播放或连续同音");
                last = sun.LastPlayedId;
                sun.CompletePlaybackForTest(); codexQueue.Drain();
                if (windows.Values.Any(w => w.IsPlaying)) throw new InvalidDataException("Codex 重复播放或触发接话链");
            }
            shao.PlayFirstForTest(); EnqueueCodexReaction("waiting"); codexQueue.Drain();
            if (sun.IsPlaying || codexQueue.PendingCount != 1) throw new InvalidDataException("Codex 未等待现有语音");
            shao.CompletePlaybackForTest(); codexQueue.Drain();
            if (sun.IsCodexReaction) throw new InvalidDataException("Codex 打断接话链");
            sun.CompletePlaybackForTest(); feng.CompletePlaybackForTest(); sun.CompletePlaybackForTest(); codexQueue.Drain();
            if (!sun.IsCodexReaction) throw new InvalidDataException("Codex 排队后未播放");
            shao.PlayFirstForTest();
            if (sun.IsPlaying || !shao.IsPlaying) throw new InvalidDataException("手动播放未停止 Codex 反应");
            StopAllPlayback();
            EnqueueCodexReaction("disable-active"); codexQueue.Drain(); EnqueueCodexReaction("disable-queued");
            codexReaction.IsChecked = false; codexQueue.Drain();
            if (sun.IsPlaying || codexQueue.PendingCount != 0) throw new InvalidDataException("关闭 Codex 未停止播放并清队列");
            codexReaction.IsChecked = true;
            EnqueueCodexReaction("stop-all"); StopAllPlayback(); codexQueue.Drain();
            if (sun.IsPlaying) throw new InvalidDataException("停止全部后仍有 Codex 反应");
            switches["sunpet"].IsChecked = false; EnqueueCodexReaction("hidden"); codexQueue.Drain();
            switches["sunpet"].IsChecked = true; codexQueue.Drain();
            if (windows["sunpet"].IsPlaying) throw new InvalidDataException("关闭孙哥期间积压 Codex 反应");
        }
        finally { sunSettings.RandomMode = wasRandom; sunSettings.PinnedId = wasPinned; StopAllPlayback(); SimulatedPlayback = false; }
    }
    public void ShowPanel()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }
}
