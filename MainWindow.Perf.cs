using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Smoothness on other people's PCs: Windows must not slow the island down as a "background app", and a
/// report with the real numbers (frame times while the island moves, screen, graphics card) can be copied
/// from Settings and sent to the developer.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ "the island froze": what was it doing?

    private long _uiBeat = Environment.TickCount64;
    private volatile string _uiStep = "start";
    private System.Threading.Timer? _stallTimer;
    private bool _stallLogged;

    /// <summary>Called on the UI thread at the start of anything that could take long: a heartbeat with a name.</summary>
    private void UiStep(string what)
    {
        _uiStep = what;
        Interlocked.Exchange(ref _uiBeat, Environment.TickCount64);
    }

    /// <summary>
    /// A background timer watches the heartbeat. If the UI thread stops answering for a few seconds, the log
    /// gets one line saying so and what it was doing — so a "the island hung" report can be acted on.
    /// </summary>
    private void InitStallWatch()
    {
        _stallTimer = new System.Threading.Timer(_ =>
        {
            long late = Environment.TickCount64 - Interlocked.Read(ref _uiBeat);
            if (late is > 4000 and < 120000)   // longer than that is the computer waking from sleep, not a hang
            {
                if (_stallLogged) return;
                _stallLogged = true;
                Log.Write($"ui thread not answering for {late / 1000} s, last step: {_uiStep}");
            }
            else _stallLogged = false;
        }, null, 3000, 1000);
    }

    // ------------------------------------------------------------------ no power throttling

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    /// <summary>
    /// Windows 11 runs programs that are never the active window in "efficiency mode" (EcoQoS: slower CPU,
    /// coarse timers) — on laptops especially. The island is never the active window, yet it animates:
    /// opt out, so its frames come on time.
    /// </summary>
    private static void DisablePowerThrottling()
    {
        try
        {
            var s = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };   // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION: both off
            if (!SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, Marshal.SizeOf<PowerThrottlingState>()))
                Log.Write("power throttling: could not opt out (" + Marshal.GetLastWin32Error() + ")");
        }
        catch (Exception e) { Log.Write("power throttling: " + e.Message); }
    }

    // ------------------------------------------------------------------ frame statistics while the island moves

    private long _perfFrames, _perfSlow, _perfVerySlow;
    private double _perfSum, _perfWorst;
    private DateTime _perfLogged = DateTime.UtcNow;

    private void RecordPerf(double dt)
    {
        if (dt <= 0 || dt > 0.5) return;   // a pause, not a frame
        _perfFrames++;
        _perfSum += dt;
        if (dt > 0.020) _perfSlow++;
        if (dt > 0.050) _perfVerySlow++;
        _perfWorst = Math.Max(_perfWorst, dt);
        // a line in the log now and then, so a log sent by a user already says how smooth it was
        if (_perfFrames % 1200 == 0 && DateTime.UtcNow - _perfLogged > TimeSpan.FromMinutes(10))
        {
            _perfLogged = DateTime.UtcNow;
            Log.Write("perf: " + PerfSummary());
        }
    }

    private string PerfSummary() => _perfFrames == 0
        ? "no animation frames yet"
        : $"{_perfFrames} frames while moving, {1 / (_perfSum / _perfFrames):0} fps avg, late (>20 ms) {_perfSlow * 100.0 / _perfFrames:0.#}%, " +
          $"very late (>50 ms) {_perfVerySlow * 100.0 / _perfFrames:0.#}%, worst {_perfWorst * 1000:0} ms";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

    private static IEnumerable<string> GraphicsCards()
    {
        for (int i = 0; i < 6; i++)
        {
            string? name = null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:0000}");
                name = key?.GetValue("DriverDesc") as string;
            }
            catch { }
            if (!string.IsNullOrEmpty(name)) yield return name;
        }
    }

    /// <summary>Everything that decides how smooth the island is on this PC, in a few lines.</summary>
    private string PerfReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Atoll {Updater.Current} · Windows {Environment.OSVersion.Version}");
        sb.AppendLine("Smoothness: " + PerfSummary());
        sb.AppendLine($"Graphics: {string.Join(" + ", GraphicsCards())} · WPF tier {RenderCapability.Tier >> 16}");
        sb.AppendLine($"CPU threads: {Environment.ProcessorCount}");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            int hz = EnumDisplaySettings(screen.DeviceName, -1, ref dm) ? dm.dmDisplayFrequency : 0;
            sb.AppendLine($"Screen: {screen.Bounds.Width}x{screen.Bounds.Height} {hz} Hz{(screen.Primary ? " (main)" : "")}");
        }
        sb.AppendLine($"Island: scale {Dpi:0.00} · size {_settings.IslandScale}% · light graphics {(Lite ? "on" : "off")}{(_slowPcSeen ? " (auto)" : "")} · glass {(_settings.Glass ? "on" : "off")} · waves {(_settings.LiquidWaves ? "on" : "off")}");
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        sb.Append($"Power: {(power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline ? "battery" : "plugged in")}");
        return sb.ToString();
    }

    private void CopyPerfReport_Click(object sender, RoutedEventArgs e)
    {
        var text = PerfReport();
        try { Clipboard.SetText(text); } catch { }
        Log.Write("perf report:\n" + text);
        ShowToast("", L.T("Отчёт скопирован"), L.T("Вставьте его в комментарии или в Telegram-канал"));
    }
}
