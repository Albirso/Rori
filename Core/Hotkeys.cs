using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Recorder;

public sealed class Hotkeys : IDisposable
{
    delegate IntPtr LlProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LlProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly HwndSource _src;
    readonly HashSet<int> _ids = new();
    readonly Dictionary<int, uint> _hooked = new();
    readonly HashSet<uint> _down = new();
    readonly LlProc _proc;
    IntPtr _hook;
    public event Action<int>? Pressed;

    public Hotkeys()
    {
        var p = new HwndSourceParameters(App.Name + "Hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
        _src = new HwndSource(p);
        _src.AddHook(Hook);
        _proc = LowLevel;
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312)
        {
            handled = true;
            Pressed?.Invoke(w.ToInt32());
        }
        return IntPtr.Zero;
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    IntPtr LowLevel(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && _hooked.Count > 0)
        {
            uint vk = (uint)Marshal.ReadInt32(l);
            int msg = w.ToInt32();
            foreach (var kv in _hooked)
            {
                if (kv.Value != vk) continue;
                if (msg is 0x0100 or 0x0104)
                {
                    if (Held(0x11) || Held(0x12) || Held(0x10) || Held(0x5B) || Held(0x5C)) break;
                    if (_down.Add(vk))
                    {
                        int id = kv.Key;
                        _src.Dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke(id)));
                    }
                    return new IntPtr(1);
                }
                if (msg is 0x0101 or 0x0105)
                {
                    if (_down.Remove(vk)) return new IntPtr(1);
                }
                break;
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    public bool Register(int id, uint mods, uint vk)
    {
        Unregister(id);
        if (vk == 0) return false;
        if (Native.RegisterHotKey(_src.Handle, id, mods | 0x4000, vk))
        {
            _ids.Add(id);
            return true;
        }
        if (vk == 0x7B && mods == 0)
        {
            _hooked[id] = vk;
            if (_hook == IntPtr.Zero) _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
            if (_hook != IntPtr.Zero) return true;
            _hooked.Remove(id);
        }
        return false;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) Native.UnregisterHotKey(_src.Handle, id);
        if (_hooked.Remove(id) && _hooked.Count == 0 && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _down.Clear();
        }
    }

    public void Dispose()
    {
        foreach (var id in _ids.Concat(_hooked.Keys).ToArray()) Unregister(id);
        _src.Dispose();
    }
}
