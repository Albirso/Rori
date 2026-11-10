using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace Recorder;

public sealed class FfProc
{
    public Process P { get; }
    readonly StringBuilder _err = new();
    public string Errors { get { lock (_err) return _err.ToString(); } }
    public bool Running { get { try { return !P.HasExited; } catch { return false; } } }

    public FfProc(string args, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo(Ffmpeg.Exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        P = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        Native.AttachChild(P);
        if (lowPriority) try { P.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        P.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_err) { if (_err.Length < 8000) _err.AppendLine(e.Data); }
        };
        P.BeginErrorReadLine();
    }

    public async Task<bool> StopAsync(int waitMs = 8000)
    {
        try
        {
            if (P.HasExited) return P.ExitCode == 0;
            try { P.StandardInput.Write("q"); P.StandardInput.Flush(); P.StandardInput.Close(); } catch { }
            using var cts = new CancellationTokenSource(waitMs);
            try { await P.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { Kill(); return false; }
            return true;
        }
        catch (Exception e) { Log.Write("ffmpeg stop: " + e.Message); return false; }
    }

    public void Kill() { try { if (!P.HasExited) P.Kill(true); } catch { } }
}

public static class Ffmpeg
{
    public static string Exe { get; private set; } = "ffmpeg";
    public static bool Found { get; private set; }
    public static bool Nvenc => Encoder == "h264_nvenc";
    public static string Encoder { get; private set; } = "libx264";
    public static bool Hardware => Encoder != "libx264";
    public static bool Dda { get; private set; }

    public static void Init()
    {
        Exe = Locate() ?? "ffmpeg";
        Found = Run("-hide_banner -version", 5000) == 0;
        if (!Found) { Log.Write("ffmpeg not found"); return; }
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            if (Run($"-hide_banner -loglevel error -f lavfi -i color=s=640x360:r=30:d=0.2 -pix_fmt nv12 {EncoderArgs(enc, 20, 60, false)}-f null -", 8000) == 0
                && Run($"-hide_banner -loglevel error -f lavfi -i color=s=640x360:r=30:d=0.2 -pix_fmt nv12 {EncoderArgs(enc, 24, 60, true)}-f null -", 8000) == 0)
            {
                Encoder = enc;
                break;
            }
        }
        Dda = Run("-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=0:framerate=30 -frames:v 2 -f null -", 8000) == 0;
        Log.Write($"ffmpeg: {Exe}; encoder={Encoder}; ddagrab={Dda}");
    }

    static int Run(string args, int waitMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true });
            if (p == null) return -1;
            p.ErrorDataReceived += (_, _) => { };
            p.OutputDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            if (!p.WaitForExit(waitMs)) { try { p.Kill(true); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static string? Locate()
    {
        var cands = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (!string.IsNullOrEmpty(exeDir)) cands.Add(Path.Combine(exeDir, "ffmpeg.exe"));
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { cands.Add(Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe")); } catch { }
        }
        cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\shims\ffmpeg.exe"));
        foreach (var c in cands)
        {
            try
            {
                if (!File.Exists(c)) continue;
                var shim = Path.ChangeExtension(c, ".shim");
                if (File.Exists(shim))
                {
                    foreach (var line in File.ReadAllLines(shim))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, "^\\s*path\\s*=\\s*\"?([^\"]+)\"?\\s*$");
                        if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
                    }
                }
                return c;
            }
            catch { }
        }
        return null;
    }

    static string EncoderArgs(string enc, int cq, int gop, bool lowLatency) => enc switch
    {
        "h264_nvenc" => $"-c:v h264_nvenc -preset {(lowLatency ? "p3" : "p4")} -rc vbr -cq {cq} -b:v 0 -g {gop} -bf 0 ",
        "h264_amf" => $"-c:v h264_amf -usage {(lowLatency ? "lowlatency" : "transcoding")} -quality balanced -rc cqp -qp_i {cq} -qp_p {cq + 2} -g {gop} -bf 0 ",
        "h264_qsv" => $"-c:v h264_qsv -preset {(lowLatency ? "veryfast" : "medium")} -global_quality {cq + 2} -g {gop} -bf 0 ",
        _ => $"-c:v libx264 -preset {(lowLatency ? "ultrafast" : "veryfast")} -crf {cq + 2} -g {gop} ",
    };

    public static Size FitInto(Size src, Size box)
    {
        double k = Math.Min((double)box.Width / src.Width, (double)box.Height / src.Height);
        int w = Math.Max(2, (int)Math.Round(src.Width * k / 2) * 2);
        int h = Math.Max(2, (int)Math.Round(src.Height * k / 2) * 2);
        return new Size(w, h);
    }

    public static int MaxFps(Size s)
    {
        long mbs = (long)((s.Width + 15) / 16) * ((s.Height + 15) / 16);
        return (int)Math.Max(1, Math.Min(720, 16_711_680 / mbs));
    }

    public static string CaptureArgs(Rectangle area, Mon? mon, int fps, int cq, bool lowLatency, string? audioUrl = null, Size? output = null)
    {
        var sb = new StringBuilder("-hide_banner -loglevel error -y ");
        var outSize = output ?? area.Size;
        bool scale = outSize != area.Size;
        string scaler = $"scale={outSize.Width}:{outSize.Height}:flags={(outSize.Width < area.Width ? "bicubic" : "bilinear")}";
        fps = Math.Clamp(fps, 1, MaxFps(outSize));
        if (!Hardware) fps = Math.Min(fps, lowLatency ? 30 : 60);
        bool dda = Dda && mon != null && mon.Index >= 0 && mon.Bounds.Contains(area);
        bool gpuFrames = false;
        if (dda)
        {
            var g = $"ddagrab=output_idx={mon!.Index}:framerate={fps}:draw_mouse=1";
            if (area != mon.Bounds)
                g += $":video_size={area.Width}x{area.Height}:offset_x={area.X - mon.Bounds.X}:offset_y={area.Y - mon.Bounds.Y}";
            if (Nvenc && !scale) gpuFrames = true;
            else g += ",hwdownload,format=bgra" + (scale ? "," + scaler : "") + ",format=nv12";
            sb.Append($"-f lavfi -i \"{g}\" ");
        }
        else
        {
            fps = Math.Min(fps, 60);
            sb.Append($"-f gdigrab -framerate {fps} -draw_mouse 1 -offset_x {area.X} -offset_y {area.Y} -video_size {area.Width}x{area.Height} -i desktop ");
        }
        if (audioUrl != null) sb.Append(AudioHub.InputArgs(audioUrl));
        sb.Append("-map 0:v ");
        if (!dda && scale) sb.Append($"-vf \"{scaler}\" ");
        if (audioUrl != null) sb.Append("-map 1:a -c:a aac -b:a 192k -af aresample=async=1000 ");
        int gop = Math.Max(2, fps * 2);
        if (!gpuFrames) sb.Append("-pix_fmt nv12 ");
        if (!lowLatency) sb.Append($"-fps_mode cfr -r {fps} ");
        sb.Append(EncoderArgs(Encoder, cq, gop, lowLatency));
        return sb.ToString();
    }
}
