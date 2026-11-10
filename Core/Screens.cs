using System.Drawing;
using System.Runtime.InteropServices;

namespace Recorder;

public sealed record Mon(int Index, Rectangle Bounds, string Device);

public static unsafe class Screens
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int L, T, R, B;
        public int Attached;
        public int Rotation;
        public IntPtr Monitor;
    }

    static void Release(IntPtr p) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)p)[2])(p);

    public static List<Mon> Outputs()
    {
        var list = new List<Mon>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
            IntPtr adapter;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[12])(factory, 0, &adapter);
            if (hr == 0)
            {
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr output;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)adapter)[7])(adapter, i, &output) != 0) break;
                    OutputDesc d;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)(*(void***)output)[7])(output, &d) == 0 && d.Attached != 0)
                        list.Add(new Mon((int)i, Rectangle.FromLTRB(d.L, d.T, d.R, d.B), new string(d.DeviceName)));
                    Release(output);
                }
                Release(adapter);
            }
            Release(factory);
        }
        catch (Exception e) { Log.Write("monitors: " + e.Message); }
        return list;
    }

    public static List<Mon> MainTwo()
    {
        var all = Outputs();
        if (all.Count == 0)
            all = System.Windows.Forms.Screen.AllScreens.Select(s => new Mon(-1, s.Bounds, s.DeviceName)).ToList();
        return all.OrderByDescending(m => (long)m.Bounds.Width * m.Bounds.Height).Take(2).OrderBy(m => m.Bounds.X).ToList();
    }
}
