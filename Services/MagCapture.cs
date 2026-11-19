using System.IO;
﻿using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// A picture of the screen WITHOUT one window (the island), taken through the Windows Magnification API:
/// its filter list leaves chosen windows out of what it draws. Unlike hiding the window from all capture,
/// the island stays visible to screenshots, recordings and screen sharing — and the glass still sees
/// what is behind it. Must be used from the UI thread. Not every Windows build delivers the picture:
/// Works says whether this one does (checked on first use).
/// </summary>
public static class MagCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGIMAGEHEADER
    {
        public uint Width, Height;
        public Guid Format;
        public uint Stride, Offset;
        public UIntPtr Size;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool ScalingCallback(IntPtr hwnd, IntPtr src, MAGIMAGEHEADER srcHeader, IntPtr dst, MAGIMAGEHEADER dstHeader, RECT unclipped, RECT clipped, IntPtr dirty);

    [DllImport("Magnification.dll")] private static extern bool MagInitialize();
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] list);
    [DllImport("Magnification.dll")] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, ScalingCallback cb);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private const int WS_EX_LAYERED = 0x80000, WS_POPUP = unchecked((int)0x80000000), WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const int MW_FILTERMODE_EXCLUDE = 0;

    private static IntPtr _host, _mag, _excluded;
    private static bool _failed;
    private static int _magW, _magH;
    private static readonly ScalingCallback Callback = OnImage;   // kept alive for as long as the process runs
    private static byte[]? _full;
    private static int _fullW, _fullH, _fullStride;
    private static bool _got;

    /// <summary>Tests: the size of the picture Windows handed over last.</summary>
    public static (int W, int H) LastSize => (_fullW, _fullH);

    /// <summary>null: not tried yet.</summary>
    public static bool? Works { get; private set; }

    private static bool OnImage(IntPtr hwnd, IntPtr src, MAGIMAGEHEADER h, IntPtr dst, MAGIMAGEHEADER dh, RECT unclipped, RECT clipped, IntPtr dirty)
    {
        try
        {
            int size = (int)(h.Stride * h.Height);
            if (size <= 0 || size > 64 * 1024 * 1024) return true;
            if (_full == null || _full.Length < size) _full = new byte[size];
            Marshal.Copy(src + (int)h.Offset, _full, 0, size);
            _fullW = (int)h.Width; _fullH = (int)h.Height; _fullStride = (int)h.Stride;
            _got = true;
        }
        catch { }
        return true;
    }

    // ---- crash guard ----
    // The Magnification API can take the whole process down from inside Windows (no exception to catch), and on
    // some PCs it does so on the very first grabs — the island then never opens while the glass is on. So: a marker
    // file is written before the first grab and removed after a few good ones (or on a normal exit). If it is
    // still there at the next start, the last run died inside this API: it stays switched off (the glass is solid).
    private static string GuardDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
    private static string PendingFile => Path.Combine(GuardDir, "mag.pending");
    private static string DisabledFile => Path.Combine(GuardDir, "mag.disabled");
    private const int GoodGrabsToTrust = 20;
    private static int _goodGrabs;
    private static bool _guardChecked;

    private static bool GuardAllows()
    {
        if (_guardChecked) return !_failed;
        _guardChecked = true;
        try
        {
            if (File.Exists(DisabledFile)) { _failed = true; return false; }
            if (File.Exists(PendingFile))
            {
                File.WriteAllText(DisabledFile, "The Magnification API crashed a previous start; delete this file to try it again.");
                File.Delete(PendingFile);
                Log.Write("magnification grab: the last start died inside it, switched off (the glass stays solid)");
                _failed = true;
                return false;
            }
            Directory.CreateDirectory(GuardDir);
            File.WriteAllText(PendingFile, DateTime.UtcNow.ToString("o"));
        }
        catch { /* no guard is better than no glass at all */ }
        return true;
    }

    /// <summary>A normal exit: whatever happened, Windows did not crash on us.</summary>
    public static void MarkClean()
    {
        try { File.Delete(PendingFile); } catch { }
    }

    private static bool Init(IntPtr exclude)
    {
        if (_failed) return false;
        if (!GuardAllows()) return false;
        if (_mag != IntPtr.Zero && _excluded == exclude) return true;
        try
        {
            if (_mag == IntPtr.Zero)
            {
                if (!MagInitialize()) { _failed = true; return false; }
                var inst = GetModuleHandle(null);
                _host = CreateWindowEx(WS_EX_LAYERED, "Static", "AtollGlassHost", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (_host == IntPtr.Zero) { _failed = true; return false; }
                // one size for good, as big as all the screens: resizing a magnifier window while Windows is
                // drawing into it crashes inside Windows itself (seen in the field), so it is never resized
                var all = System.Windows.Forms.SystemInformation.VirtualScreen;
                _magW = Math.Max(all.Width, 640); _magH = Math.Max(all.Height, 480);
                _mag = CreateWindowEx(0, "Magnifier", "AtollGlass", WS_CHILD | WS_VISIBLE, 0, 0, _magW, _magH, _host, IntPtr.Zero, inst, IntPtr.Zero);
                if (_mag == IntPtr.Zero || !MagSetImageScalingCallback(_mag, Callback)) { _failed = true; return false; }
            }
            if (!MagSetWindowFilterList(_mag, MW_FILTERMODE_EXCLUDE, 1, [exclude])) { _failed = true; return false; }
            _excluded = exclude;
            return true;
        }
        catch { _failed = true; return false; }
    }

    /// <summary>
    /// The screen area r without the window "exclude", shrunk to (w, h) pixels, as BGRA bytes — or null where
    /// this Windows doesn't deliver the picture.
    /// </summary>
    public static byte[]? Capture(IntPtr exclude, System.Drawing.Rectangle r, int w, int h, byte[]? buffer)
    {
        if (w < 1 || h < 1 || r.Width < 1 || r.Height < 1 || !Init(exclude)) { if (_failed) Works = false; return null; }
        try
        {
            _got = false;
            if (!Native.IsWindow(_mag)) { _mag = IntPtr.Zero; Works = false; _failed = true; return null; }   // gone (the program is closing)
            if (r.Width > _magW || r.Height > _magH) return null;   // bigger than the screens: nothing sensible to grab
            if (!MagSetWindowSource(_mag, new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom }) || !_got || _full == null)
            {
                Works = false; _failed = true;
                return null;
            }
            Works = true;
            if (++_goodGrabs == GoodGrabsToTrust) MarkClean();   // survived the first grabs: Windows copes with it here
            if (buffer == null || buffer.Length != w * h * 4) buffer = new byte[w * h * 4];
            // box-shrink the full picture to w × h (it is blurred afterwards anyway)
            int srcW = Math.Min(_fullW, r.Width), srcH = Math.Min(_fullH, r.Height);
            double sx = (double)srcW / w, sy = (double)srcH / h;
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Min(srcH - 1, (int)(y * sy)), y1 = Math.Max(y0 + 1, Math.Min(srcH, (int)((y + 1) * sy)));
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(srcW - 1, (int)(x * sx)), x1 = Math.Max(x0 + 1, Math.Min(srcW, (int)((x + 1) * sx)));
                    int b = 0, g = 0, rr = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * _fullStride;
                        for (int xx = x0; xx < x1; xx++) { int i = row + xx * 4; b += _full[i]; g += _full[i + 1]; rr += _full[i + 2]; n++; }
                    }
                    int o = (y * w + x) * 4;
                    buffer[o] = (byte)(b / n); buffer[o + 1] = (byte)(g / n); buffer[o + 2] = (byte)(rr / n); buffer[o + 3] = 255;
                }
            }
            return buffer;
        }
        catch { Works = false; _failed = true; return null; }
    }
}
