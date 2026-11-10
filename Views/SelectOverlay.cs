using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Recorder;

public sealed class SelectOverlay : Window
{
    readonly System.Drawing.Rectangle _vs;
    readonly Path _dim;
    readonly Rectangle _frame;
    readonly Border _label;
    readonly TextBlock _labelTb;
    readonly Border _hint;
    Point? _start;
    bool _done;

    public event Action<System.Drawing.Rectangle?>? Finished;

    public SelectOverlay()
    {
        _vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Title = App.Name;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0; Width = 400; Height = 300;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.Resources["Ui"];

        var accent = Ui.C("Accent");
        var stage = new Grid { Width = _vs.Width, Height = _vs.Height };
        stage.Children.Add(new Image { Source = Snapshot(), Stretch = Stretch.Fill });
        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)), IsHitTestVisible = false };
        stage.Children.Add(_dim);
        var canvas = new Canvas { IsHitTestVisible = false };
        _frame = new Rectangle { Stroke = new SolidColorBrush(accent), StrokeThickness = 2, Visibility = Visibility.Collapsed, RadiusX = 2, RadiusY = 2 };
        canvas.Children.Add(_frame);
        _labelTb = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Text") };
        _label = new Border
        {
            Child = _labelTb, Padding = new Thickness(12, 5, 12, 6), CornerRadius = new CornerRadius(10),
            Background = Ui.A("Surface", 0xF2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        canvas.Children.Add(_label);
        _hint = new Border
        {
            Child = new TextBlock { Text = "Drag to select a region. Press Esc to cancel.", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Text") },
            Padding = new Thickness(22, 11, 22, 12), CornerRadius = new CornerRadius(22),
            Background = Ui.A("Surface", 0xF2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1),
        };
        canvas.Children.Add(_hint);
        stage.Children.Add(canvas);
        Content = new Viewbox { Stretch = Stretch.Fill, Child = stage };
        SetDim(Rect.Empty);

        var scr = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        _hint.Loaded += (_, _) =>
        {
            Canvas.SetLeft(_hint, scr.X - _vs.X + (scr.Width - _hint.ActualWidth) / 2);
            Canvas.SetTop(_hint, scr.Y - _vs.Y + 36);
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h)?.AddHook(Pin);
            Native.SetWindowPos(h, Native.Topmost, _vs.X, _vs.Y, _vs.Width, _vs.Height, Native.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) => BringToFront();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_done) return;
            _start = Cur();
            CaptureMouse();
            _hint.Visibility = Visibility.Collapsed;
            Update();
        };
        PreviewMouseMove += (_, _) => { if (_start != null) Update(); };
        PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; Finish(); };
        PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; End(null); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) End(null); };
        Deactivated += (_, _) => { if (!_done) Dispatcher.BeginInvoke(BringToFront); };
    }

    void BringToFront()
    {
        if (_done) return;
        try
        {
            Activate();
            Focus();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }
        catch { }
    }

    IntPtr Pin(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046, WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED) { handled = true; return IntPtr.Zero; }
        if (msg == WM_WINDOWPOSCHANGING && lp != IntPtr.Zero)
        {
            var p = Marshal.PtrToStructure<Native.WINDOWPOS>(lp);
            p.X = _vs.X; p.Y = _vs.Y; p.Cx = _vs.Width; p.Cy = _vs.Height;
            p.Flags &= ~(Native.SWP_NOSIZE | Native.SWP_NOMOVE);
            Marshal.StructureToPtr(p, lp, false);
        }
        return IntPtr.Zero;
    }

    BitmapSource Snapshot()
    {
        using var bmp = new System.Drawing.Bitmap(_vs.Width, _vs.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(_vs.X, _vs.Y, 0, 0, bmp.Size);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }

    Point Cur()
    {
        Native.GetCursorPos(out var p);
        return new Point(Math.Clamp(p.X - _vs.X, 0, _vs.Width), Math.Clamp(p.Y - _vs.Y, 0, _vs.Height));
    }

    Rect Sel() => _start is { } a ? new Rect(a, Cur()) : Rect.Empty;

    void SetDim(Rect hole)
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new RectangleGeometry(new Rect(0, 0, _vs.Width, _vs.Height)));
        if (!hole.IsEmpty && hole.Width > 0 && hole.Height > 0) g.Children.Add(new RectangleGeometry(hole));
        _dim.Data = g;
    }

    void Update()
    {
        var r = Sel();
        SetDim(r);
        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, r.X - 2);
        Canvas.SetTop(_frame, r.Y - 2);
        _frame.Width = r.Width + 4;
        _frame.Height = r.Height + 4;
        _labelTb.Text = $"{(int)r.Width & ~1} × {(int)r.Height & ~1}";
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = _label.DesiredSize.Width, lh = _label.DesiredSize.Height;
        double y = r.Y - lh - 8;
        if (y < 4) y = r.Bottom + 8;
        if (y + lh > _vs.Height - 4) y = r.Y + 8;
        Canvas.SetLeft(_label, Math.Clamp(r.X, 4, Math.Max(4, _vs.Width - lw - 4)));
        Canvas.SetTop(_label, y);
    }

    void Finish()
    {
        if (_start == null || _done) return;
        var r = Sel();
        ReleaseMouseCapture();
        int w = (int)r.Width & ~1, h = (int)r.Height & ~1;
        if (w < 32 || h < 32)
        {
            _start = null;
            _frame.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Visible;
            SetDim(Rect.Empty);
            return;
        }
        End(new System.Drawing.Rectangle((int)r.X + _vs.X, (int)r.Y + _vs.Y, w, h));
    }

    void End(System.Drawing.Rectangle? r)
    {
        if (_done) return;
        _done = true;
        try { ReleaseMouseCapture(); } catch { }
        Hide();
        Close();
        Finished?.Invoke(r);
    }

    public void Cancel() => End(null);
}
