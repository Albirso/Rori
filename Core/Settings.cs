using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Recorder;

public static class Log
{
    static readonly object Gate = new();

    public static string Dir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App.Name);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static void Write(string s)
    {
        try
        {
            lock (Gate)
            {
                var f = Path.Combine(Dir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 1_000_000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine);
            }
        }
        catch { }
    }
}

public sealed class Settings
{
    public int BufferMinutes { get; set; } = 1;
    public bool BufferEnabled { get; set; } = true;
    public string Theme { get; set; } = "";
    public uint RegionVk { get; set; } = 0x72;
    public uint RegionMods { get; set; }
    public uint BufferVk { get; set; } = 0x7B;
    public uint BufferMods { get; set; }

    public int RegionFps { get; set; } = 240;
    public string Resolution { get; set; } = "2k";
    [JsonIgnore] public int BufferFps => 60;

    public int Cq(bool buffer) => buffer ? 24 : 18;

    public static System.Drawing.Size ResolutionBox(string r) => r switch
    {
        "1k" => new(1920, 1080),
        "4k" => new(3840, 2160),
        _ => new(2560, 1440),
    };

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Recordings");
    public static string RegionDir => Path.Combine(Root, "Screen recording");
    public static string BufferDir => Path.Combine(Root, "Recorder otp");

    static string FilePath => Path.Combine(Log.Dir, "settings.json");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) { Log.Write("settings: " + e.Message); s = new Settings(); }
        s.BufferMinutes = Math.Clamp(s.BufferMinutes, 1, 5);
        s.RegionFps = Math.Clamp(s.RegionFps, 1, 720);
        if (s.Resolution is not ("1k" or "2k" or "4k")) s.Resolution = "2k";
        if (s.RegionVk == 0) s.RegionVk = 0x72;
        if (s.BufferVk == 0) s.BufferVk = 0x7B;
        if (string.IsNullOrEmpty(s.Theme) || Themes.All.All(t => t.Id != s.Theme))
        {
            s.Theme = SystemTheme();
            s.Save();
        }
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write("settings save: " + e.Message); }
    }

    static string SystemTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0 ? "black" : "white";
        }
        catch { }
        return "white";
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Autostart
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(App.Name) is string; }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue(App.Name, "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue(App.Name, false);
            }
            catch (Exception e) { Log.Write("autostart: " + e.Message); }
        }
    }

    public static void RefreshAutostartPath()
    {
        if (Autostart) Autostart = true;
    }

    public static string KeyName(uint mods, uint vk)
    {
        var s = "";
        if ((mods & 2) != 0) s += "Ctrl + ";
        if ((mods & 1) != 0) s += "Alt + ";
        if ((mods & 4) != 0) s += "Shift + ";
        if ((mods & 8) != 0) s += "Win + ";
        var name = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk).ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        return s + name;
    }
}
