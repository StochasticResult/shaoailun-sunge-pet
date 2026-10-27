using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShaolanPet;

sealed class PanelSettings
{
    public bool AutoReply { get; set; } = true;
}

sealed class ControlPanel : Window
{
    private readonly Dictionary<string, List<Clip>> clips;
    private readonly Dictionary<string, Settings> settings = new();
    private readonly Dictionary<string, PetWindow> windows = new();
    private readonly Dictionary<string, CheckBox> switches = new();
    private readonly Dictionary<string, Slider> sliders = new();
    private readonly Dictionary<string, TextBlock> values = new();
    private readonly PanelSettings panelSettings;
    private readonly Conversation conversation;
    private readonly CheckBox autoReply;
    private static string PanelSettingsPath => Path.Combine(Program.SettingsRoot, "panel.json");
    private bool shuttingDown;
    private bool updating;

    public ControlPanel(Dictionary<string, List<Clip>> clips)
    {
        this.clips = clips;
        panelSettings = LoadPanelSettings();
        conversation = new Conversation(panelSettings.AutoReply,
            id => windows.TryGetValue(id, out var window) && !window.IsPlaying,
            turn => windows.TryGetValue(turn.Speaker, out var window) && window.PlayReply(turn));
        Title = "桌宠控制面板";
        Width = 460; Height = 480; MinWidth = 420; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(248, 249, 252));
        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        page.Children.Add(new TextBlock { Text = "桌宠控制面板", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black });
        page.Children.Add(new TextBlock { Text = "选择要显示的桌宠，分别调节声音。", Margin = new Thickness(0, 5, 0, 16), Foreground = Brushes.DimGray });
        foreach (var pet in Program.Pets)
        {
            var current = pet.LoadSettings();
            current.Volume = Math.Clamp(double.IsFinite(current.Volume) ? current.Volume : .65, 0, 1);
            settings.Add(pet.Id, current);
            var card = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)), BorderThickness = new Thickness(1) };
            var content = new StackPanel();
            var on = new CheckBox { Content = pet.Name + "桌宠", IsChecked = current.Enabled, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center };
            switches.Add(pet.Id, on);
            on.Checked += (_, _) => { if (!updating) SetEnabled(pet, true); };
            on.Unchecked += (_, _) => { if (!updating) SetEnabled(pet, false); };
            content.Children.Add(on);
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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var stop = new Button { Content = "停止所有声音", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 0) };
        stop.Click += (_, _) => { conversation.Cancel(); foreach (var window in windows.Values) window.StopPlayback(); };
        var quit = new Button { Content = "退出全部", Padding = new Thickness(12, 5, 12, 5) };
        quit.Click += (_, _) => Close();
        buttons.Children.Add(stop); buttons.Children.Add(quit); page.Children.Add(buttons);
        page.Children.Add(new TextBlock { Text = "点击人物播放，拖动移动；右键可选择句子和大小。", Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { foreach (var pet in Program.Pets) if (settings[pet.Id].Enabled) OpenPet(pet); };
        Closing += (_, _) => { shuttingDown = true; conversation.Cancel(); };
    }

    private static PanelSettings LoadPanelSettings()
    {
        try { return JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(PanelSettingsPath), Program.JsonOptions) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void SetAutoReply(bool enabled)
    {
        panelSettings.AutoReply = enabled;
        conversation.SetEnabled(enabled);
        try {
            Directory.CreateDirectory(Program.SettingsRoot);
            File.WriteAllText(PanelSettingsPath, JsonSerializer.Serialize(panelSettings, Program.JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { MessageBox.Show("设置无法保存：" + e.Message, "桌宠控制面板", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public ConversationTurn StartConversation(string id) => conversation.Start(id);
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
    // Called by --self-test with an isolated settings directory on a Windows runner.
    public void VerifyControls()
    {
        if (windows.Count != Program.Pets.Length) throw new InvalidDataException("桌宠未全部启动");
        autoReply.IsChecked = false;
        if (LoadPanelSettings().AutoReply) throw new InvalidDataException("联动开关未保存");
        autoReply.IsChecked = true;
        if (!LoadPanelSettings().AutoReply) throw new InvalidDataException("联动开关未恢复");
        foreach (var pet in Program.Pets)
        {
            switches[pet.Id].IsChecked = false;
            if (windows.ContainsKey(pet.Id) || settings[pet.Id].Enabled || pet.LoadSettings().Enabled)
                throw new InvalidDataException(pet.Name + "关闭失败");
            sliders[pet.Id].Value = 25;
            if (Math.Abs(pet.LoadSettings().Volume - .25) > .001)
                throw new InvalidDataException(pet.Name + "音量未保存");
            switches[pet.Id].IsChecked = true;
            if (!windows.ContainsKey(pet.Id) || !pet.LoadSettings().Enabled)
                throw new InvalidDataException(pet.Name + "重新开启失败");
        }
        windows[Program.Pets[0].Id].Close();
        if (switches[Program.Pets[0].Id].IsChecked != false)
            throw new InvalidDataException("关闭桌宠未同步面板");
    }
    public void ShowPanel()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }
}
