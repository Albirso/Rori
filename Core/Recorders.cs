using System.Drawing;
using System.Globalization;
using System.Text;

namespace Recorder;

public sealed class RegionRecorder
{
    FfProc? _ff;
    AudioPipe? _audio;
    public string? File { get; private set; }
    public bool Active => _ff != null;
    public event Action<string>? Died;

    public void Start(Rectangle area, Settings s)
    {
        Directory.CreateDirectory(Settings.RegionDir);
        var now = DateTime.Now;
        var stamp = now.ToString("yyyy-MM-dd HH-mm-ss");
        var path = Path.Combine(Settings.RegionDir, stamp + ".mp4");
        for (int i = 2; System.IO.File.Exists(path); i++)
            path = Path.Combine(Settings.RegionDir, $"{stamp} ({i}).mp4");
        File = path;
        var mon = Screens.Outputs().FirstOrDefault(m => m.Bounds.Contains(area));
        CloseAudio();
        _audio = AudioHub.Available ? AudioHub.Open("region") : null;
        try
        {
            var outSize = Ffmpeg.FitInto(area.Size, Settings.ResolutionBox(s.Resolution));
            var args = Ffmpeg.CaptureArgs(area, mon, s.RegionFps, s.Cq(false), false, _audio?.Url, outSize) + $"-movflags +faststart \"{path}\"";
            Log.Write("F3 start: " + args);
            var ff = new FfProc(args);
            _ff = ff;
            ff.P.EnableRaisingEvents = true;
            ff.P.Exited += (_, _) =>
            {
                if (_ff != ff) return;
                _ff = null;
                CloseAudio();
                Log.Write("F3: ffmpeg exited on its own: " + ff.Errors);
                Died?.Invoke(ff.Errors);
            };
            if (ff.P.HasExited && _ff == ff)
            {
                _ff = null;
                CloseAudio();
                throw new InvalidOperationException("ffmpeg exited immediately: " + ff.Errors);
            }
        }
        catch
        {
            CloseAudio();
            throw;
        }
    }

    void CloseAudio()
    {
        var a = _audio;
        _audio = null;
        a?.Dispose();
    }

    public async Task<string?> StopAsync()
    {
        var ff = _ff;
        _ff = null;
        if (ff == null) return null;
        await ff.StopAsync();
        CloseAudio();
        var f = File;
        if (f != null && System.IO.File.Exists(f) && new FileInfo(f).Length > 1024) return f;
        Log.Write("F3: file was not created: " + ff.Errors);
        return null;
    }

    public async Task CancelAsync()
    {
        var ff = _ff;
        _ff = null;
        if (ff == null) return;
        await ff.StopAsync(4000);
        CloseAudio();
        try { if (File != null && System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch { }
    }
}

public sealed class BufferRecorder
{
    const int SegSec = 10;

    sealed class Lane
    {
        public Mon Mon = null!;
        public int No;
        public FfProc? Ff;
        public AudioPipe? Audio;
        public DateTime LastStart;
        public int Fails;
    }

    readonly List<Lane> _lanes = new();
    readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    Settings _s = new();
    bool _on;
    bool _saving;

    public bool Running => _on;

    public static string TempDir => Path.Combine(Log.Dir, "buffer");

    public BufferRecorder() { _timer.Tick += (_, _) => Tick(); }

    public void Start(Settings s)
    {
        Stop();
        _s = s;
        if (!s.BufferEnabled || !Ffmpeg.Found) return;
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        Directory.CreateDirectory(TempDir);
        int no = 1;
        foreach (var m in Screens.MainTwo()) _lanes.Add(new Lane { Mon = m, No = no++ });
        _on = true;
        foreach (var l in _lanes) Launch(l);
        _timer.Start();
    }

    void Launch(Lane l)
    {
        try
        {
            l.Audio?.Dispose();
            l.Audio = AudioHub.Available ? AudioHub.Open("buf" + l.No) : null;
            var pattern = Path.Combine(TempDir, $"m{l.No}_%Y%m%d-%H%M%S.ts").Replace('\\', '/');
            var args = Ffmpeg.CaptureArgs(l.Mon.Bounds, l.Mon, _s.BufferFps, _s.Cq(true), true, l.Audio?.Url)
                + $"-force_key_frames \"expr:gte(t,n_forced*{SegSec})\" -flush_packets 1 "
                + $"-f segment -segment_time {SegSec} -segment_format mpegts -reset_timestamps 1 -strftime 1 \"{pattern}\"";
            l.LastStart = DateTime.Now;
            l.Ff = new FfProc(args, lowPriority: true);
            Log.Write($"buffer, monitor {l.No}: " + args);
        }
        catch (Exception e) { Log.Write("buffer start: " + e.Message); }
    }

    void Tick()
    {
        if (!_on || _saving) return;
        var fresh = Screens.MainTwo();
        if (fresh.Count > 0)
        {
            if (!fresh.Select(m => (m.Index, m.Device)).SequenceEqual(_lanes.Select(l => (l.Mon.Index, l.Mon.Device))))
            {
                Log.Write("buffer: monitors changed to " + string.Join(", ", fresh.Select(m => m.Bounds)));
                Start(_s);
                return;
            }
            for (int i = 0; i < _lanes.Count; i++) _lanes[i].Mon = fresh[i];
        }
        foreach (var l in _lanes)
        {
            if (l.Ff is { Running: true })
            {
                if ((DateTime.Now - l.LastStart).TotalSeconds < SegSec * 3) continue;
                if (!Stalled(l)) { l.Fails = 0; continue; }
                if (l.Fails < 3) Log.Write($"buffer, monitor {l.No} stalled, restarting");
                l.Ff.Kill();
            }
            else if (l.Ff != null && l.Fails < 3) Log.Write($"buffer, monitor {l.No} stopped: " + l.Ff.Errors);
            int wait = Math.Min(60, 5 * (1 << Math.Min(l.Fails, 4)));
            if ((DateTime.Now - l.LastStart).TotalSeconds < wait) continue;
            l.Fails++;
            Launch(l);
        }
        Cleanup();
    }

    static bool Stalled(Lane l)
    {
        try
        {
            var newest = Directory.GetFiles(TempDir, $"m{l.No}_*.ts").Select(SegTime).Max();
            return newest == null || newest < DateTime.Now.AddSeconds(-SegSec * 3);
        }
        catch { return false; }
    }

    void Cleanup()
    {
        try
        {
            var limit = DateTime.Now.AddSeconds(-(_s.BufferMinutes * 60 + SegSec * 3));
            foreach (var f in Directory.GetFiles(TempDir, "m*.ts"))
                if (SegTime(f) is { } t && t < limit) try { System.IO.File.Delete(f); } catch { }
        }
        catch { }
    }

    static DateTime? SegTime(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file);
        int i = n.IndexOf('_');
        if (i < 0) return null;
        return DateTime.TryParseExact(n[(i + 1)..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    public void Stop()
    {
        _on = false;
        _timer.Stop();
        foreach (var l in _lanes)
        {
            l.Ff?.Kill();
            l.Audio?.Dispose();
        }
        _lanes.Clear();
    }

    static string LaneName(int no, int count) => count < 2 ? "Monitor" : no == 1 ? "1 - left monitor" : "2 - right monitor";

    public async Task<(string? dir, int saved)> SaveAsync()
    {
        if (!_on || _saving) return (null, 0);
        _saving = true;
        try
        {
            var now = DateTime.Now;
            var from = now.AddSeconds(-_s.BufferMinutes * 60);
            var dir = Path.Combine(Settings.BufferDir, now.ToString("yyyy-MM-dd HH-mm-ss"));
            for (int i = 2; Directory.Exists(dir); i++)
                dir = Path.Combine(Settings.BufferDir, now.ToString("yyyy-MM-dd HH-mm-ss") + $" ({i})");
            Directory.CreateDirectory(dir);
            var lanes = _lanes.Select(l => l.No).ToList();
            var jobs = lanes.Select(no => Task.Run(() => SaveLane(no, lanes.Count, dir, from, now))).ToArray();
            var ok = await Task.WhenAll(jobs);
            int saved = ok.Count(x => x);
            if (saved == 0)
            {
                try { Directory.Delete(dir, true); } catch { }
                return (null, 0);
            }
            return (dir, saved);
        }
        catch (Exception e) { Log.Write("F12: " + e); return (null, 0); }
        finally { _saving = false; }
    }

    bool SaveLane(int no, int count, string dir, DateTime from, DateTime now)
    {
        try
        {
            var segs = Directory.GetFiles(TempDir, $"m{no}_*.ts")
                .Select(f => (f, t: SegTime(f)))
                .Where(x => x.t != null && new FileInfo(x.f).Length > 0)
                .Select(x => (x.f, t: x.t!.Value))
                .OrderBy(x => x.t)
                .ToList();
            if (segs.Count == 0) return false;
            var pick = new List<(string f, DateTime t)>();
            for (int i = 0; i < segs.Count; i++)
            {
                var end = i + 1 < segs.Count ? segs[i + 1].t : now;
                if (end > from) pick.Add(segs[i]);
            }
            if (pick.Count == 0) return false;
            double skip = Math.Max(0, (from - pick[0].t).TotalSeconds);
            var list = Path.Combine(TempDir, $"list{no}.txt");
            System.IO.File.WriteAllLines(list, pick.Select(p => "file '" + p.f.Replace('\\', '/') + "'"), new UTF8Encoding(false));
            var outFile = Path.Combine(dir, LaneName(no, count) + ".mp4");
            var args = "-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + list + "\" "
                + (skip > 0.5 ? "-ss " + skip.ToString("0.###", CultureInfo.InvariantCulture) + " " : "")
                + "-c copy -movflags +faststart \"" + outFile + "\"";
            var ff = new FfProc(args);
            if (!ff.P.WaitForExit(60000)) { ff.Kill(); return false; }
            bool good = System.IO.File.Exists(outFile) && new FileInfo(outFile).Length > 1024;
            if (!good) Log.Write($"F12, monitor {no}: " + ff.Errors);
            return good;
        }
        catch (Exception e) { Log.Write("F12 join: " + e.Message); return false; }
    }
}
