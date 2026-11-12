using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WF = System.Windows.Forms;

namespace Recorder;

public partial class App : Application
{
    public const string Name = "Rori";
    const int HkRegion = 1, HkBuffer = 2, HkEscape = 3;

    public Settings Settings { get; private set; } = new();
    public string? Problem { get; private set; }

    Mutex? _mutex;
    Hotkeys? _hotkeys;
    WF.NotifyIcon? _tray;
    System.Drawing.Icon? _icoIdle, _icoRec;
    ContextMenu? _menu;
    SettingsWindow? _settingsWin;
    DateTime _settingsClosed;
    readonly RegionRecorder _region = new();
    readonly BufferRecorder _buffer = new();
    SelectOverlay? _overlay;
    RecPill? _pill;
    RegionFrame? _frame;
    bool _busy;
    bool _savingBuffer;
    string? _ffProblem;
    string? _hkProblem;

    string RegionKey => Settings.KeyName(Settings.RegionMods, Settings.RegionVk);
    string BufferKey => Settings.KeyName(Settings.BufferMods, Settings.BufferVk);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, Name + ".SingleInstance", out bool first);
        if (!first)
        {
            Native.AllowSetForegroundWindow(-1);
            try { EventWaitHandle.OpenExisting(Name + ".ShowSettings").Set(); } catch { }
            Shutdown();
            return;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".ShowSettings");
        new Thread(() =>
        {
            while (true)
            {
                show.WaitOne();
                Dispatcher.BeginInvoke(() =>
                {
                    if (_settingsWin != null) return;
                    _settingsClosed = DateTime.MinValue;
                    ToggleSettings();
                });
            }
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, a) => { Log.Write("error: " + a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("fatal: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Write("task: " + a.Exception); a.SetObserved(); };

        MigrateFromRecorder();
        Settings = Settings.Load();
        Themes.Apply(Settings.Theme);
        Settings.RefreshAutostartPath();
        try
        {
            Directory.CreateDirectory(Settings.RegionDir);
            Directory.CreateDirectory(Settings.BufferDir);
        }
        catch (Exception ex) { Log.Write("folders: " + ex.Message); }

        _icoIdle = LoadAppIcon();
        _icoRec = MakeRecIcon();
        _tray = new WF.NotifyIcon { Icon = _icoIdle, Visible = true, Text = Name };
        _tray.MouseUp += (_, a) =>
        {
            if (a.Button == WF.MouseButtons.Left) ToggleSettings();
            else if (a.Button == WF.MouseButtons.Right) ShowMenu();
        };

        _hotkeys = new Hotkeys();
        _hotkeys.Pressed += id =>
        {
            if (id == HkRegion) OnRegionKey();
            else if (id == HkBuffer) OnBufferKey();
            else if (id == HkEscape) _overlay?.Cancel();
        };
        RegisterHotkeys();
        _region.Died += _ => Dispatcher.BeginInvoke(() =>
        {
            CloseRecUi();
            Toast.Show("Recording stopped", "An error occurred while recording.", null, true);
        });

        Task.Run(Ffmpeg.Init).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (!Ffmpeg.Found)
            {
                _ffProblem = "FFmpeg not found. Recording is unavailable.";
                UpdateProblem();
                Toast.Show("FFmpeg not found", "Recording is unavailable.", null, true);
            }
            _buffer.Start(Settings);
            UpdateTip();
        }));

        if (Problem != null) Toast.Show(Name + " is running", Problem, null, true);
        else Toast.Show(Name + " is running", $"{RegionKey} to record a region. {BufferKey} to save a replay.");
    }

    static void MigrateFromRecorder()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldDir = Path.Combine(local, "Recorder");
            var newDir = Path.Combine(local, Name);
            if (!Directory.Exists(oldDir)) return;
            var oldSettings = Path.Combine(oldDir, "settings.json");
            var newSettings = Path.Combine(newDir, "settings.json");
            if (File.Exists(oldSettings) && !File.Exists(newSettings))
            {
                Directory.CreateDirectory(newDir);
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(oldSettings)) as System.Text.Json.Nodes.JsonObject;
                json?.Remove("Theme");
                File.WriteAllText(newSettings, json?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "{}");
            }
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k?.GetValue("Recorder") != null)
                {
                    k.DeleteValue("Recorder", false);
                    k.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
                }
            }
            try { Directory.Delete(oldDir, true); } catch { }
        }
        catch (Exception e) { Log.Write("migrate: " + e.Message); }
    }

    public void SuspendHotkeys()
    {
        _hotkeys?.Unregister(HkRegion);
        _hotkeys?.Unregister(HkBuffer);
    }

    public void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        var busy = new List<string>();
        if (!_hotkeys.Register(HkRegion, Settings.RegionMods, Settings.RegionVk)) busy.Add(RegionKey);
        if (!_hotkeys.Register(HkBuffer, Settings.BufferMods, Settings.BufferVk)) busy.Add(BufferKey);
        _hkProblem = busy.Count switch
        {
            0 => null,
            1 => $"{busy[0]} is already in use by another app.",
            _ => $"{busy[0]} and {busy[1]} are already in use by another app.",
        };
        if (_hkProblem != null) Log.Write(_hkProblem);
        UpdateProblem();
        UpdateTip();
    }

    void UpdateProblem()
    {
        var parts = new[] { _ffProblem, _hkProblem }.Where(x => x != null).ToArray();
        Problem = parts.Length > 0 ? string.Join("\n", parts) : null;
    }

    void UpdateTip()
    {
        if (_tray == null) return;
        _tray.Text = _region.Active ? Name + " - Recording" : Name;
        _tray.Icon = _region.Active ? _icoRec : _icoIdle;
    }

    public void SettingsChanged(bool restartBuffer)
    {
        Settings.Save();
        if (restartBuffer) _buffer.Start(Settings);
    }

    async void OnRegionKey()
    {
        try
        {
            if (_busy) return;
            if (_overlay != null) { _overlay.Cancel(); return; }
            if (_region.Active) { await StopRegion(true); return; }
            if (!Ffmpeg.Found) { Toast.Show("Recording is unavailable", "FFmpeg not found.", null, true); return; }
            _settingsWin?.Close();
            var ov = new SelectOverlay();
            _overlay = ov;
            _hotkeys?.Register(HkEscape, 0, 0x1B);
            ov.Finished += async r =>
            {
                _hotkeys?.Unregister(HkEscape);
                _overlay = null;
                if (r == null) return;
                _busy = true;
                try
                {
                    await Task.Delay(150);
                    StartRegion(r.Value);
                }
                finally { _busy = false; }
            };
            ov.Show();
        }
        catch (Exception e)
        {
            Log.Write("F3: " + e);
            _hotkeys?.Unregister(HkEscape);
            _overlay = null;
            _busy = false;
        }
    }

    void StartRegion(System.Drawing.Rectangle area)
    {
        try
        {
            _region.Start(area, Settings);
            _frame = new RegionFrame(area);
            _frame.Show();
            _pill = new RecPill(area, RegionKey);
            _pill.StopClicked += async () => await StopRegion(true);
            _pill.CancelClicked += async () => await StopRegion(false);
            _pill.Show();
            UpdateTip();
        }
        catch (Exception e)
        {
            Log.Write("F3 start: " + e);
            CloseRecUi();
            Toast.Show("Couldn't start recording", null, null, true);
        }
    }

    void CloseRecUi()
    {
        try { _pill?.Close(); } catch { }
        try { _frame?.Close(); } catch { }
        _pill = null;
        _frame = null;
        UpdateTip();
    }

    async Task StopRegion(bool save)
    {
        if (_busy || !_region.Active) return;
        _busy = true;
        try
        {
            CloseRecUi();
            if (save)
            {
                var f = await _region.StopAsync();
                if (f != null) Toast.Show("Recording saved", "Click to show in folder", f);
                else Toast.Show("Couldn't save recording", null, null, true);
            }
            else
            {
                await _region.CancelAsync();
                Toast.Show("Recording discarded");
            }
        }
        finally
        {
            _busy = false;
            UpdateTip();
        }
    }

    async void OnBufferKey()
    {
        if (_savingBuffer) return;
        if (!Settings.BufferEnabled || !_buffer.Running)
        {
            Toast.Show("Replay buffer is off", "Turn it on in Settings.", null, true);
            return;
        }
        _savingBuffer = true;
        try
        {
            var (dir, _) = await _buffer.SaveAsync();
            if (dir != null) Toast.Show("Replay saved", "Click to show in folder", dir);
            else Toast.Show("Couldn't save replay", "Not enough footage yet.", null, true);
        }
        finally { _savingBuffer = false; }
    }

    void ToggleSettings()
    {
        if (_settingsWin != null) { _settingsWin.Close(); return; }
        if ((DateTime.Now - _settingsClosed).TotalMilliseconds < 350) return;
        var w = new SettingsWindow(this);
        _settingsWin = w;
        w.Closed += (_, _) =>
        {
            if (_settingsWin == w) _settingsWin = null;
            _settingsClosed = DateTime.Now;
        };
        w.Show();
        w.Activate();
    }

    void ShowMenu()
    {
        if (_menu != null) _menu.IsOpen = false;
        var menu = new ContextMenu();
        _menu = menu;
        void Add(string icon, string text, string gesture, Action act)
        {
            var mi = new MenuItem { Header = text, Tag = icon, InputGestureText = gesture };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        bool rec = _region.Active;
        Add(rec ? "" : "", rec ? "Stop recording" : "Record region", RegionKey, OnRegionKey);
        Add("", "Save replay", BufferKey, OnBufferKey);
        menu.Items.Add(new Separator());
        Add("", "Settings", "", () => { _settingsClosed = DateTime.MinValue; if (_settingsWin == null) ToggleSettings(); });
        Add("", "Open recordings folder", "", () => OpenFolder(Settings.Root));
        menu.Items.Add(new Separator());
        Add("", "Exit", "", Quit);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource src) Native.SetForegroundWindow(src.Handle);
        };
        menu.Closed += (_, _) => { if (_menu == menu) _menu = null; };
        menu.IsOpen = true;
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
        }
        catch (Exception e) { Log.Write("folder: " + e.Message); }
    }

    async void Quit()
    {
        try
        {
            _overlay?.Cancel();
            CloseRecUi();
            if (_region.Active)
            {
                var f = await _region.StopAsync();
                if (f != null) Log.Write("saved on exit: " + f);
            }
            _buffer.Stop();
            try { if (Directory.Exists(BufferRecorder.TempDir)) Directory.Delete(BufferRecorder.TempDir, true); } catch { }
        }
        catch { }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _hotkeys?.Dispose();
        Shutdown();
    }

    static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var s = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (s != null)
            {
                var size = WF.SystemInformation.SmallIconSize;
                return new System.Drawing.Icon(s, size.Width, size.Height);
            }
        }
        catch (Exception e) { Log.Write("icon: " + e.Message); }
        return MakeRecIcon();
    }

    static System.Drawing.Icon MakeRecIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF4, 0x3F, 0x5E));
            g.FillEllipse(red, 2, 2, 28, 28);
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillRectangle(white, 11, 11, 10, 10);
        }
        var h = bmp.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
        finally { Native.DestroyIcon(h); }
    }
}
