using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Recorder;

static class Ui
{
    public static Brush B(string key) => (Brush)Application.Current.Resources[key];
    public static Color C(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    public static SolidColorBrush A(string key, byte a)
    {
        var c = C(key);
        return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    public static void MakeOverlay(Window w, bool clickThrough = false)
    {
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Background = Brushes.Transparent;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.Topmost = true;
        w.ShowActivated = false;
        w.FontFamily = (FontFamily)Application.Current.Resources["Ui"];
        w.Foreground = B("Text");
        w.UseLayoutRounding = true;
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            if (clickThrough) ex |= Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
            if (Environment.GetEnvironmentVariable("RECORDER_SHOW_IN_CAPTURE") != "1")
                Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
        };
    }

    public static System.Windows.Media.Effects.DropShadowEffect Shadow(double blur, double depth, double opacity) =>
        new() { BlurRadius = blur, ShadowDepth = depth, Opacity = opacity, Color = Colors.Black };
}

public sealed class RegionFrame : Window
{
    public RegionFrame(System.Drawing.Rectangle area)
    {
        Ui.MakeOverlay(this, clickThrough: true);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0; Width = 100; Height = 100;
        Content = new Border { BorderBrush = Ui.B("Rec"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3), Opacity = 0.9 };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h)?.AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x02E0) handled = true;
                return IntPtr.Zero;
            });
            Native.SetWindowPos(h, Native.Topmost, area.X - 3, area.Y - 3, area.Width + 6, area.Height + 6, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        };
    }
}

public sealed class RecPill : Window
{
    readonly TextBlock _time = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 1), MinWidth = 42 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DateTime _started = DateTime.Now;
    public event Action? StopClicked;
    public event Action? CancelClicked;

    public RecPill(System.Drawing.Rectangle area, string stopKey)
    {
        Ui.MakeOverlay(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000;

        var dot = new Ellipse { Width = 10, Height = 10, Fill = Ui.B("Rec"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _time.Text = "00:00";

        var iconStyle = (Style)Application.Current.Resources["IconBtn"];
        var stop = new Button { Style = iconStyle, Content = "", ToolTip = $"Stop ({stopKey})", Foreground = Ui.B("Text"), Width = 28, Height = 28 };
        stop.Click += (_, _) => StopClicked?.Invoke();
        var cancel = new Button { Style = iconStyle, Content = "", ToolTip = "Discard", Foreground = Ui.B("Dim"), Width = 28, Height = 28 };
        cancel.Click += (_, _) => CancelClicked?.Invoke();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(_time);
        row.Children.Add(stop);
        row.Children.Add(cancel);
        Content = new Border
        {
            Child = row, Margin = new Thickness(8), Padding = new Thickness(14, 5, 6, 5), CornerRadius = new CornerRadius(19),
            Background = Ui.A("Surface", 0xF5), BorderBrush = Ui.A("Rec", 0x90), BorderThickness = new Thickness(1),
            Effect = Ui.Shadow(12, 2, 0.4),
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button) try { DragMove(); } catch { }
        };

        _timer.Tick += (_, _) =>
        {
            var t = DateTime.Now - _started;
            _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => Place(area);
    }

    void Place(System.Drawing.Rectangle a)
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(h, out var wr);
        int w = wr.R - wr.L, ht = wr.B - wr.T;
        var scr = System.Windows.Forms.Screen.FromRectangle(a).Bounds;
        int cx = Math.Clamp(a.X + (a.Width - w) / 2, scr.X, Math.Max(scr.X, scr.Right - w));
        int cy = Math.Clamp(a.Y + (a.Height - ht) / 2, scr.Y, Math.Max(scr.Y, scr.Bottom - ht));
        System.Drawing.Point p;
        if (a.Bottom + ht <= scr.Bottom - 48) p = new(cx, a.Bottom);
        else if (a.Y - ht >= scr.Y) p = new(cx, a.Y - ht);
        else if (a.Right + w <= scr.Right) p = new(a.Right, cy);
        else if (a.X - w >= scr.X) p = new(a.X - w, cy);
        else
        {
            var other = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).Where(b => !b.IntersectsWith(a)).OrderByDescending(b => b.Width * b.Height).FirstOrDefault();
            p = other.Width > 0 ? new(other.X + (other.Width - w) / 2, other.Y + 24) : new(scr.Right - w - 16, scr.Y + 16);
        }
        Native.SetWindowPos(h, Native.Topmost, p.X, p.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

public sealed class Toast : Window
{
    static Toast? _current;

    public static void Show(string title, string? text = null, string? openPath = null, bool error = false)
    {
        try
        {
            _current?.Close();
            var t = new Toast(title, text, openPath, error);
            _current = t;
            t.Show();
        }
        catch (Exception e) { Log.Write("toast: " + e.Message); }
    }

    Toast(string title, string? text, string? openPath, bool error)
    {
        Ui.MakeOverlay(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000;
        var sp = new StackPanel { MaxWidth = 300 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = error ? Ui.B("Rec") : Ui.B("Accent"), Margin = new Thickness(0, 1, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(head);
        if (!string.IsNullOrEmpty(text))
            sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Ui.B("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 3, 0, 0) });
        Content = new Border
        {
            Child = sp, Margin = new Thickness(14), Padding = new Thickness(16, 11, 18, 12), CornerRadius = new CornerRadius(14),
            Background = Ui.A("Surface", 0xFA), BorderBrush = Ui.B("Border"), BorderThickness = new Thickness(1),
            Effect = Ui.Shadow(18, 3, 0.45),
        };
        if (openPath != null)
        {
            Cursor = Cursors.Hand;
            MouseLeftButtonUp += (_, _) => { App.OpenFolder(openPath); Close(); };
        }
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 6;
            Top = wa.Bottom - ActualHeight - 6;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 6 : 3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
        Closed += (_, _) =>
        {
            timer.Stop();
            if (_current == this) _current = null;
        };
    }
}
