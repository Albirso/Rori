using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Recorder;

public static class AudioHub
{
    public const int Rate = 48000, Channels = 2;
    const int BytesPerFrame = Channels * 4;

    static readonly object Gate = new();
    static readonly List<AudioPipe> Sinks = new();
    static readonly Stopwatch Clock = new();
    static WasapiLoopbackCapture? _cap;
    static string? _deviceId;
    static Thread? _pump;
    static bool _on;
    static long _framesOut;
    static DateTime _lastData = DateTime.MinValue;

    public static bool Available { get; private set; } = true;

    static void EnsureStarted()
    {
        if (_on) return;
        _on = true;
        Clock.Restart();
        _framesOut = 0;
        StartCapture();
        _pump = new Thread(Pump) { IsBackground = true, Name = "audio pump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    static void StartCapture()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var cap = new WasapiLoopbackCapture(dev);
            var fmt = cap.WaveFormat;
            Log.Write($"audio: {dev.FriendlyName}, {fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample} bit, {fmt.Encoding}");
            var conv = new Converter(fmt);
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var data = conv.Convert(e.Buffer, e.BytesRecorded);
                if (data.Length > 0) Push(data, fromDevice: true);
            };
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Write("audio stopped: " + e.Exception.Message);
                lock (Gate) { if (_cap == cap) _cap = null; }
                try { cap.Dispose(); } catch { }
            };
            cap.StartRecording();
            lock (Gate)
            {
                _cap = cap;
                _deviceId = dev.ID;
            }
            Available = true;
        }
        catch (Exception e)
        {
            Log.Write("audio failed to start: " + e.Message);
            lock (Gate) _deviceId = null;
        }
    }

    static void Pump()
    {
        var lastCheck = DateTime.Now;
        while (_on)
        {
            Thread.Sleep(10);
            try
            {
                long expected = (long)(Clock.Elapsed.TotalSeconds * Rate);
                long behind;
                DateTime last;
                lock (Gate)
                {
                    behind = expected - _framesOut;
                    last = _lastData;
                }
                if (behind > Rate * 60 / 1000 && (DateTime.Now - last).TotalMilliseconds > 60)
                {
                    long n = behind - Rate * 30 / 1000;
                    Push(new byte[n * BytesPerFrame], fromDevice: false);
                }
                if ((DateTime.Now - lastCheck).TotalSeconds >= 2)
                {
                    lastCheck = DateTime.Now;
                    string? id = null;
                    try
                    {
                        using var en = new MMDeviceEnumerator();
                        id = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
                    }
                    catch { }
                    WasapiLoopbackCapture? old;
                    string? curId;
                    lock (Gate)
                    {
                        old = _cap;
                        curId = _deviceId;
                    }
                    if (id != null && (old == null || id != curId))
                    {
                        try { old?.StopRecording(); } catch { }
                        StartCapture();
                    }
                }
            }
            catch (Exception e) { Log.Write("audio pump: " + e.Message); }
        }
    }

    static void Push(byte[] data, bool fromDevice)
    {
        AudioPipe[] sinks;
        lock (Gate)
        {
            if (fromDevice)
            {
                _lastData = DateTime.Now;
                long expected = (long)(Clock.Elapsed.TotalSeconds * Rate);
                long frames = data.Length / BytesPerFrame;
                long ahead = _framesOut + frames - expected;
                long allow = Rate * 80 / 1000;
                if (ahead > allow)
                {
                    long cut = ahead - allow;
                    if (cut >= frames) return;
                    data = data.AsSpan((int)cut * BytesPerFrame).ToArray();
                }
            }
            _framesOut += data.Length / BytesPerFrame;
            sinks = Sinks.ToArray();
        }
        foreach (var s in sinks) s.Enqueue(data);
    }

    public static AudioPipe Open(string tag)
    {
        var p = new AudioPipe(tag);
        lock (Gate)
        {
            EnsureStarted();
            Sinks.Add(p);
        }
        return p;
    }

    internal static void Remove(AudioPipe p)
    {
        lock (Gate) Sinks.Remove(p);
    }

    public static string InputArgs(string url) =>
        $"-thread_queue_size 4096 -f f32le -ar {Rate} -ac {Channels} -i \"{url}\" ";

    sealed class Converter
    {
        readonly bool _float;
        readonly int _bytes, _ch, _rate;
        readonly double _step;
        double _pos;
        float _prevL, _prevR;

        public Converter(WaveFormat f)
        {
            _ch = Math.Max(1, f.Channels);
            _bytes = Math.Max(1, f.BitsPerSample / 8);
            _rate = f.SampleRate;
            _float = f.Encoding == WaveFormatEncoding.IeeeFloat
                  || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            _step = (double)f.SampleRate / Rate;
        }

        float Sample(byte[] b, int off)
        {
            if (_float) return BitConverter.ToSingle(b, off);
            return _bytes switch
            {
                2 => BitConverter.ToInt16(b, off) / 32768f,
                3 => ((b[off] << 8 | b[off + 1] << 16 | b[off + 2] << 24) >> 8) / 8388608f,
                4 => BitConverter.ToInt32(b, off) / 2147483648f,
                _ => 0,
            };
        }

        public byte[] Convert(byte[] buf, int len)
        {
            int frame = _bytes * _ch;
            int n = len / frame;
            if (n == 0) return Array.Empty<byte>();
            var l = new float[n];
            var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * frame;
                l[i] = Sample(buf, o);
                r[i] = _ch > 1 ? Sample(buf, o + _bytes) : l[i];
            }
            if (_rate == Rate)
            {
                var outb = new byte[n * BytesPerFrame];
                for (int i = 0; i < n; i++)
                {
                    BitConverter.TryWriteBytes(outb.AsSpan(i * 8), l[i]);
                    BitConverter.TryWriteBytes(outb.AsSpan(i * 8 + 4), r[i]);
                }
                return outb;
            }
            var res = new List<byte>(n * BytesPerFrame * 2);
            while (_pos < n)
            {
                int i = (int)_pos;
                double t = _pos - i;
                float al = i == 0 ? _prevL : l[i - 1], ar = i == 0 ? _prevR : r[i - 1];
                float bl = l[i], br = r[i];
                res.AddRange(BitConverter.GetBytes((float)(al + (bl - al) * t)));
                res.AddRange(BitConverter.GetBytes((float)(ar + (br - ar) * t)));
                _pos += _step;
            }
            _pos -= n;
            _prevL = l[n - 1];
            _prevR = r[n - 1];
            return res.ToArray();
        }
    }
}

public sealed class AudioPipe : IDisposable
{
    readonly NamedPipeServerStream _srv;
    readonly BlockingCollection<byte[]> _q = new(new ConcurrentQueue<byte[]>());
    volatile bool _connected, _closed;
    public string Url { get; }

    public AudioPipe(string tag)
    {
        var name = $"{App.Name.ToLowerInvariant()}_{tag}_{Environment.ProcessId}_{Guid.NewGuid():N}";
        Url = @"\\.\pipe\" + name;
        _srv = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        new Thread(Run) { IsBackground = true, Name = "audio pipe " + tag }.Start();
    }

    public void Enqueue(byte[] data)
    {
        if (_closed || !_connected) return;
        if (_q.Count > 2000) return;
        try { _q.Add(data); } catch (InvalidOperationException) { }
    }

    void Run()
    {
        try
        {
            var t = _srv.WaitForConnectionAsync();
            if (!t.Wait(20000)) { Log.Write("audio: ffmpeg did not connect to the pipe"); return; }
            _connected = true;
            foreach (var d in _q.GetConsumingEnumerable()) _srv.Write(d, 0, d.Length);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or AggregateException) { }
        catch (Exception e) { Log.Write("audio pipe: " + e.Message); }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        AudioHub.Remove(this);
        try { _q.CompleteAdding(); } catch { }
        try { _srv.Dispose(); } catch { }
    }
}
