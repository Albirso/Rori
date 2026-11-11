using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace Recorder;

public static class Package
{
    const string TaskId = "RoriStartup";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    static bool? _packaged;

    public static bool IsPackaged
    {
        get
        {
            if (_packaged is { } p) return p;
            try
            {
                int len = 0;
                _packaged = GetCurrentPackageFullName(ref len, null) != 15700;
            }
            catch { _packaged = false; }
            return _packaged.Value;
        }
    }

    static StartupTask Task() => StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult();

    public static bool StartupEnabled() => Task().State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    public static void SetStartup(bool on)
    {
        var t = Task();
        if (on)
        {
            var state = t.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            Log.Write("startup task: " + state);
        }
        else t.Disable();
    }
}
