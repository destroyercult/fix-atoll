using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Services;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace DynamicIsland;

/// <summary>
/// Volume and screen brightness: while either changes (keys, the mixer, a headset wheel), a small level
/// shows beside the island — the same little pill Caps Lock uses, so nothing on the island folds.
/// </summary>
public partial class MainWindow
{
    private readonly MMDeviceEnumerator _levelDevices = new();
    private MMDevice? _volumeDevice;
    private string _volumeDeviceId = "";
    private float _volumeWas = -1;
    private bool _muteWas;

    /// <summary>Every few seconds: follow the default output (headphones plugged in) and listen to its volume.</summary>
    private void WatchVolumeDevice()
    {
        if (!_settings.ShowLevels || _demoMedia != null) return;
        try
        {
            var d = _levelDevices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (d.ID == _volumeDeviceId) return;
            if (_volumeDevice != null) try { _volumeDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
            _volumeDevice = d;
            _volumeDeviceId = d.ID;
            var v = d.AudioEndpointVolume;
            _volumeWas = v.MasterVolumeLevelScalar;   // a new device is not a volume change
            _muteWas = v.Mute;
            v.OnVolumeNotification += OnVolume;
        }
        catch { _volumeDeviceId = ""; _volumeDevice = null; }
    }

    /// <summary>Called by Windows on its own thread.</summary>
    private void OnVolume(AudioVolumeNotificationData data)
    {
        float vol = data.MasterVolume;
        bool mute = data.Muted;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_settings.ShowLevels) return;
            // Windows also reports changes of one channel's balance and repeats itself: only real changes count
            if (Math.Abs(vol - _volumeWas) < 0.004 && mute == _muteWas) return;
            _volumeWas = vol;
            _muteWas = mute;
            ShowLevel(mute || vol < 0.005 ? "\uE74F" : vol < 0.34 ? "\uE993" : vol < 0.67 ? "\uE994" : "\uE995",
                      mute ? L.T("Звук выключен") : L.T("Громкость"), mute ? 0 : vol, "volume");
        });
    }

    private void ShowLevel(string glyph, string title, double level, string kind) =>
        ShowKeyPill(glyph, title, $"{Math.Round(level * 100):0}%", null, level, kind);

    // ------------------------------------------------------------------ screen brightness (laptops)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

    private const uint IoctlQueryBrightness = 0x00230498;   // IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS
    private SafeFileHandle? _lcd;
    private bool _noLcd;
    private int _brightnessWas = -1;

    /// <summary>The built-in screen's brightness, 0…100, or -1 (a desktop monitor has none to ask about).</summary>
    private int ReadBrightness()
    {
        if (_noLcd) return -1;
        try
        {
            if (_lcd == null || _lcd.IsInvalid)
            {
                _lcd = CreateFile(@"\\.\LCD", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (_lcd.IsInvalid) { _noLcd = true; return -1; }
            }
            var buf = new byte[3];   // policy, on mains, on battery
            if (!DeviceIoControl(_lcd, IoctlQueryBrightness, IntPtr.Zero, 0, buf, 3, out uint got, IntPtr.Zero) || got < 3) { _noLcd = true; return -1; }
            bool mains = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline;
            return mains ? buf[1] : buf[2];
        }
        catch { _noLcd = true; return -1; }
    }

    /// <summary>~4 times a second (from OnTick).</summary>
    private void WatchBrightness()
    {
        if (!_settings.ShowLevels || _noLcd || _demoMedia != null) return;
        int b = ReadBrightness();
        if (b < 0) return;
        if (_brightnessWas >= 0 && b != _brightnessWas) ShowLevel("\uE706", L.T("Яркость"), b / 100.0, "brightness");
        _brightnessWas = b;
    }
}
