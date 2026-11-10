using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Recorder;

public partial class SettingsWindow : Window
{
    readonly App _app;
    Settings S => _app.Settings;
    Button? _capturing;
    bool _closing;
    DateTime _popupClosed;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Fill(MinutesRow, new[] { 1, 2, 3, 4, 5 }, S.BufferMinutes, v => { S.BufferMinutes = v; _app.SettingsChanged(false); });
        BufferOn.IsChecked = S.BufferEnabled;
        ShowBufferState();
        AutoOn.IsChecked = Settings.Autostart;
        ShowKeys();
        ShowMsg();
        BuildThemeList();
        ThemePopup.Closed += (_, _) => PopupClosed();
        BuildRecSettings();
        Closing += (_, _) => _closing = true;

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth + 2;
            Top = wa.Bottom - ActualHeight + 2;
            Activate();
        };
        Deactivated += (_, _) => { if (!_closing && !ThemePopup.IsOpen) CloseMe(); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { if (_capturing != null) _app.RegisterHotkeys(); };
    }

    void Fill(UniformGrid row, int[] values, int current, Action<int> set)
    {
        foreach (var value in values)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Seg"), Content = value + " min", IsChecked = value == current, GroupName = row.Name };
            rb.Checked += (_, _) => set(value);
            row.Children.Add(rb);
        }
    }

    void ShowBufferState()
    {
        MinutesRow.IsEnabled = S.BufferEnabled;
        MinutesRow.Opacity = S.BufferEnabled ? 1 : 0.45;
    }

    void BuildThemeList()
    {
        ThemeList.Children.Clear();
        foreach (var t in Themes.All)
        {
            bool on = t.Id == Themes.Current.Id;
            var swatch = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            swatch.Children.Add(new Ellipse { Fill = new SolidColorBrush(Themes.C(t.Surface)), Stroke = new SolidColorBrush(Themes.C(t.Border)), StrokeThickness = 1.5 });
            swatch.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Themes.C(t.Accent)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            var name = new TextBlock { Text = t.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal };
            var check = new TextBlock { Text = on ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 1);
            Grid.SetColumn(check, 2);
            grid.Children.Add(swatch);
            grid.Children.Add(name);
            grid.Children.Add(check);

            var row = new Border { Child = grid, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 10, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            if (on) row.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            row.MouseEnter += (_, _) => { if (!on) row.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            row.MouseLeave += (_, _) => { if (!on) row.Background = Brushes.Transparent; };
            var id = t.Id;
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (S.Theme == id) return;
                S.Theme = id;
                S.Save();
                Themes.Apply(id);
                BuildThemeList();
            };
            ThemeList.Children.Add(row);
        }
    }

    void PopupClosed()
    {
        _popupClosed = DateTime.Now;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || IsActive || ThemePopup.IsOpen) return;
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.GetCursorPos(out var p);
            Native.GetWindowRect(h, out var r);
            if (p.X >= r.L && p.X < r.R && p.Y >= r.T && p.Y < r.B) Activate();
            else CloseMe();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    void Toggle(Popup p)
    {
        if (p.IsOpen) { p.IsOpen = false; return; }
        if ((DateTime.Now - _popupClosed).TotalMilliseconds < 250) return;
        p.IsOpen = true;
    }

    void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        RecOpen = false;
        Toggle(ThemePopup);
    }

    bool RecOpen
    {
        get => RecCard.Visibility == Visibility.Visible;
        set
        {
            if (!value && RecOpen) CommitFps();
            RecCard.Visibility = RecBackdrop.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void RecBtn_Click(object sender, RoutedEventArgs e)
    {
        ThemePopup.IsOpen = false;
        RecOpen = !RecOpen;
    }

    void RecBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        RecOpen = false;
        e.Handled = true;
    }

    bool _syncing;

    void BuildRecSettings()
    {
        foreach (var (id, text) in new[] { ("1k", "1K"), ("2k", "2K"), ("4k", "4K") })
        {
            var box = Settings.ResolutionBox(id);
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Seg"), Content = text, GroupName = "res",
                IsChecked = S.Resolution == id, ToolTip = $"{box.Width} × {box.Height}",
            };
            rb.Checked += (_, _) =>
            {
                S.Resolution = id;
                _app.SettingsChanged(false);
                ShowFpsNote();
            };
            ResRow.Children.Add(rb);
        }
        SetFps(S.RegionFps);
        FpsSlider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            SetFps((int)Math.Round(e.NewValue));
        };
        FpsBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(FpsBox, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string t || !t.All(char.IsDigit)) e.CancelCommand();
        });
        FpsBox.LostKeyboardFocus += (_, _) => CommitFps();
        FpsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitFps(); FpsBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Up) { SetFps(S.RegionFps + 1); e.Handled = true; }
            else if (e.Key == Key.Down) { SetFps(S.RegionFps - 1); e.Handled = true; }
        };
        FpsBox.GotKeyboardFocus += (_, _) => FpsBox.SelectAll();
    }

    void CommitFps()
    {
        if (int.TryParse(FpsBox.Text, out var v)) SetFps(v);
        else SetFps(S.RegionFps);
    }

    void SetFps(int v)
    {
        v = Math.Clamp(v, 1, 720);
        _syncing = true;
        FpsSlider.Value = v;
        FpsBox.Text = v.ToString();
        _syncing = false;
        if (S.RegionFps != v)
        {
            S.RegionFps = v;
            _app.SettingsChanged(false);
        }
        ShowFpsNote();
    }

    void ShowFpsNote()
    {
        int max = Ffmpeg.MaxFps(Settings.ResolutionBox(S.Resolution));
        string? note = null;
        if (S.RegionFps > max) note = $"Up to {max} fps at {S.Resolution.ToUpperInvariant()}.";
        else if (Ffmpeg.Found && !Ffmpeg.Hardware && S.RegionFps > 60) note = "Up to 60 fps on this PC.";
        RecNote.Text = note ?? "";
        RecNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowKeys()
    {
        KeyRegion.Content = Settings.KeyName(S.RegionMods, S.RegionVk);
        KeyBuffer.Content = Settings.KeyName(S.BufferMods, S.BufferVk);
    }

    void ShowMsg()
    {
        var m = _app.Problem;
        Msg.Text = m ?? "";
        Msg.Visibility = string.IsNullOrEmpty(m) ? Visibility.Collapsed : Visibility.Visible;
    }

    void Key_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing != null) { EndCapture(); return; }
        _capturing = (Button)sender;
        _capturing.Content = "Press a key…";
        _app.SuspendHotkeys();
    }

    void EndCapture()
    {
        _capturing = null;
        _app.RegisterHotkeys();
        ShowKeys();
        ShowMsg();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_capturing == null)
        {
            if (e.Key == Key.Escape)
            {
                if (ThemePopup.IsOpen) ThemePopup.IsOpen = false;
                else if (RecOpen) RecOpen = false;
                else CloseMe();
            }
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { EndCapture(); return; }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) { EndCapture(); return; }
        uint mods = 0;
        var km = Keyboard.Modifiers;
        if (km.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (km.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (km.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (km.HasFlag(ModifierKeys.Windows)) mods |= 8;
        bool region = _capturing == KeyRegion;
        bool clash = region ? vk == S.BufferVk && mods == S.BufferMods : vk == S.RegionVk && mods == S.RegionMods;
        if (clash) { EndCapture(); return; }
        if (region) { S.RegionVk = vk; S.RegionMods = mods; }
        else { S.BufferVk = vk; S.BufferMods = mods; }
        S.Save();
        EndCapture();
    }

    void BufferOn_Click(object sender, RoutedEventArgs e)
    {
        S.BufferEnabled = BufferOn.IsChecked == true;
        ShowBufferState();
        _app.SettingsChanged(true);
    }

    void AutoOn_Click(object sender, RoutedEventArgs e)
    {
        Settings.Autostart = AutoOn.IsChecked == true;
        AutoOn.IsChecked = Settings.Autostart;
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        App.OpenFolder(Settings.Root);
        CloseMe();
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseMe();

    void CloseMe()
    {
        if (_closing) return;
        _closing = true;
        ThemePopup.IsOpen = false;
        try { Close(); } catch (InvalidOperationException) { }
    }
}
