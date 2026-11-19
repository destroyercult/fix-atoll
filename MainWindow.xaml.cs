using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Services;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

/// <summary>Core: island states, spring morph, floating drag / swipe-to-hide, fullscreen hiding, hotkeys, tray, toasts.</summary>
public partial class MainWindow : Window
{
    private enum Mode { Compact, Expanded, Toast, Claude, Drop, Command }

    /// <summary>Floating pill keeps a small gap; docked it grows straight out of the screen edge.</summary>
    private double IslandTopMargin => _settings.Docked ? 0 : 8;
    private const double CompactHeight = 36;

    private readonly Settings _settings = Settings.Current;
    private readonly Random _rng = new();

    // size morph: slightly underdamped = soft liquid overshoot
    private readonly Spring _w = new(200) { Stiffness = 340, Damping = 31 };
    private readonly Spring _h = new(CompactHeight) { Stiffness = 340, Damping = 31 };
    private readonly Spring _y = new(0) { Stiffness = 300, Damping = 24 };
    private bool _rendering;
    private TimeSpan _lastFrame;

    private Mode _mode = Mode.Compact;
    private bool _hidden;          // swiped away
    private bool _fullscreenHidden;
    private bool _pinnedOpen;      // --expand (screenshots / debugging)

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _gaze = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _blink = new();
    private readonly DispatcherTimer _expandDelay = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private int _tickCount;

    private Forms.NotifyIcon? _tray;
    private IntPtr _hwnd;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _shrinkTimer.Tick += (_, _) => ShrinkNow();
        // Esc folds the open island (when nothing inside used the key first, e.g. the app search)
        KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled || _mode != Mode.Expanded) return;
            _pinnedOpen = false;
            SetMode(Mode.Compact);
            e.Handled = true;
        };
        ShadowHost.RenderTransform = Island.RenderTransform;   // the shadow moves, squashes and bounces with the island
        Services.Loc.TranslateTree(this);
        if (ContextMenu != null) Services.Loc.TranslateTree(ContextMenu);

        PlaceWindow();
        _tick.Tick += (_, _) => OnTick();
        _gaze.Tick += (_, _) => UpdateGaze();
        _blink.Tick += (_, _) => Blink();
        // hover: not while typing (the cursor just happens to sit there), and on the tab that fits the moment
        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); if (_mode == Mode.Compact && !_dragging && !Typing) OpenSmart(); };
        _collapseDelay.Tick += (_, _) => { _collapseDelay.Stop(); TryCollapse(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (_mode == Mode.Toast && !Island.IsMouseOver) SetMode(Mode.Compact); };
        _watch.Tick += (_, _) => WatchFullscreen();

        SourceInitialized += (_, _) => OnSourceInitialized();
        Deactivated += (_, _) =>
        {
            if (_mode == Mode.Expanded && !Island.IsMouseOver) _collapseDelay.Start();
            if (_mode == Mode.Command) CloseCommandBar();
        };
        Loaded += async (_, _) => await OnLoadedAsync(args);
        Closing += (_, _) => Shutdown();
        // Windows is shutting down / the user signs out: close properly instead of being killed
        Application.Current.SessionEnding += (_, _) => { if (!_shutDown) Close(); };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private async Task OnLoadedAsync(string[] args)
    {
        int snap = Array.IndexOf(args, "--snapshot");
        int promo = Array.IndexOf(args, "--promo");
        int review = Array.IndexOf(args, "--review");
        int live = Array.IndexOf(args, "--live-demo");
        if (snap >= 0 || promo >= 0 || review >= 0 || live >= 0 || args.Contains("--cover")) BeginDemo();
        if (live >= 0) StartLiveDemo(args.ElementAtOrDefault(live + 1) ?? "demo-commands.txt");

        // the last start never got as far as showing the island (a crash or a hang while it set itself up):
        // switch light graphics on before anything with glass or effects runs — the same as doing it by hand
        bool safeStart = StartupGuard.PreviousFailed && _settings.LiteGraphics != true && !Settings.ReadOnly;
        if (safeStart)
        {
            _settings.LiteGraphics = true;
            _settings.Save();
            Log.Write("the last start did not finish: light graphics switched on");
        }
        // …and once the island has been up for a few seconds, this start counts as good
        var upTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        upTimer.Tick += (_, _) => { upTimer.Stop(); StartupGuard.Done(); };
        upTimer.Start();

        ApplyThemeLocal();
        Character.Kind = _settings.Character;
        InitGlass();
        UpdateShape(false);
        InitHome();
        InitSettingsPage();
        InitBuddyReactions();
        InitUpdates();
        InitControlCenter();
        InitApps();
        ApplyPhoto();
        ApplyScene();
        ApplyLook();
        ApplyIslandScale();
        InitClaude();
        InitRecording();
        InitExtras();

        OnTick();
        _tick.Start();
        _ = Dispatcher.InvokeAsync(ShrinkWindowToPill, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _gaze.Start();
        _watch.Start();
        ScheduleBlink();
        CreateTray();

        if (safeStart)
            _ = Dispatcher.InvokeAsync(() => ShowToast("", L.T("Включена облегчённая графика"), L.T("Прошлый запуск не удался — эффекты выключены. Вернуть: Настройки → Поведение.")),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        if (_settings.Hidden) HideIsland(animate: false);

        int i = Array.IndexOf(args, "--expand");
        if (i >= 0)
        {
            _pinnedOpen = true;
            Expand();
            if (i + 1 < args.Length)
            {
                var tab = args[i + 1] switch
                {
                    "music" => TabMusic, "weather" => TabWeather, "chat" => TabChat,
                    "timer" => TabTimer, "clip" => TabClip, "notes" => TabNotes,
                    "calendar" => TabCalendar, "system" => TabSystem,
                    "settings" => TabSettings, _ => TabHome,
                };
                tab.IsChecked = true;
            }
        }

        // --run <action> [seconds]: trigger a quick action (testing without touching the keyboard)
        int r = Array.IndexOf(args, "--run");
        if (r >= 0 && r + 1 < args.Length)
        {
            var action = args[r + 1];
            int secs = r + 2 < args.Length && int.TryParse(args[r + 2], out var s) ? s : 0;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                if (action.StartsWith("lang:")) { SwitchLanguage(action[5..]); return; }   // test the language switch
                if (action == "glass")   // test: is the island opaque while the frosted picture doesn't fit it?
                {
                    Settings.ReadOnly = true;
                    _settings.Glass = true; ApplyGlass();
                    string State() => $"frost covers={_frostCovers}, body alpha={((LinearGradientBrush)IslandShape.Fill).GradientStops[1].Color.A}, bottom alpha={((LinearGradientBrush)IslandShape.Fill).GradientStops[3].Color.A}";
                    await Task.Delay(5000);
                    Log.Write("glass: folded, settled: " + State());
                    TabHome.IsChecked = true; _pinnedOpen = true; Expand();
                    await Task.Delay(60);
                    Log.Write("glass: opening: " + State());
                    await Task.Delay(1500);
                    Log.Write("glass: open, settled: " + State());
                    _pinnedOpen = false; SetMode(Mode.Compact);
                    await Task.Delay(60);
                    Log.Write("glass: closing: " + State());
                    await Task.Delay(1500);
                    Log.Write("glass: folded again: " + State());
                    await Task.Delay(5000);
                    Log.Write("glass: folded, 6 s later: " + State());
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "weathertest")   // test: the weather fallbacks for places Open-Meteo can't serve
                {
                    Settings.ReadOnly = true;
                    foreach (var fb in new[] { false, true })
                        foreach (var city in new[] { "Москва", "Новозыбков", "Kyiv" })
                        {
                            var ws = new WeatherService { TestFallback = fb };
                            try
                            {
                                var place = await ws.ResolveAsync(city);
                                if (place == null) { Log.Write($"weathertest: fallback={fb} {city}: not found"); continue; }
                                var w = await ws.GetAsync(place);
                                Log.Write($"weathertest: fallback={fb} {city} -> {place.Name} ({place.Lat:0.00},{place.Lon:0.00}) via {ws.Source}: {w.Temp:0.0}° feels {w.FeelsLike:0.0}°, {WeatherService.Describe(w.Code)}, min {w.Min:0} max {w.Max:0}, wind {w.WindSpeed:0.0}, hum {w.Humidity:0}, press {w.PressureMm:0}, sun {w.Sunrise:HH:mm}-{w.Sunset:HH:mm}, hours {w.Hours.Count} first {w.Hours.FirstOrDefault()?.Time:HH:mm} {w.Hours.FirstOrDefault()?.Temp:0}°");
                            }
                            catch (Exception e) { Log.Write($"weathertest: fallback={fb} {city}: {e.Message}"); }
                            await Task.Delay(1100);   // OpenStreetMap: at most one request a second
                        }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "keypill")   // pictures of the Caps Lock / layout pill, folded and open (with --cover)
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    await Task.Delay(6000);
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    SetMode(Mode.Compact); await Task.Delay(1200);
                    ShowKeyPill("", L.T("Caps Lock включён"), null, Color.FromRgb(0xFF, 0x9F, 0x0A));
                    await Task.Delay(400); Shot("pill-compact");
                    TabMusic.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(1500);
                    ShowKeyPill("", "EN", "English", null);
                    await Task.Delay(400); Shot("pill-open");
                    Log.Write($"keypill: mode after = {_mode}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "capturetest")   // test: does the glass's screen grab see the island itself without the capture exclusion?
                {
                    Settings.ReadOnly = true;
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    _settings.ExpandOnHover = false;
                    TabHome.IsChecked = true; _pinnedOpen = true; Expand();
                    await Task.Delay(2500);
                    var tl = Island.PointToScreen(new Point(0, 0));
                    var br = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
                    var rect = new System.Drawing.Rectangle((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));
                    foreach (var hide in new[] { true, false })
                    {
                        _settings.CaptureHidden = hide; ApplyCaptureExclusion();
                        await Task.Delay(800);
                        int w = rect.Width / 2, h = rect.Height / 2;
                        var px = CaptureService.CaptureSmall(rect, w, h, null)!;
                        double sum = 0; for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2];
                        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                        CaptureService.SavePng(bmp, System.IO.Path.Combine(dir, $"grab-exclude-{hide}.png"));
                        Log.Write($"capturetest: exclude={hide}: mean brightness {sum / (px.Length / 4 * 3):0}");
                    }
                    // the pulse: exclude only for the grab
                    foreach (var flush in new[] { false, true, false, true })
                    {
                        int w = rect.Width / 2, h = rect.Height / 2;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
                        if (flush) DwmFlush();
                        var px = CaptureService.CaptureSmall(rect, w, h, null)!;
                        Native.SetWindowDisplayAffinity(_hwnd, 0);
                        double sum = 0; for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2];
                        Log.Write($"capturetest: pulse flush={flush}: mean brightness {sum / (px.Length / 4 * 3):0}, {sw.Elapsed.TotalMilliseconds:0.0} ms");
                        await Task.Delay(500);
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "newui")   // pictures of the 1.8 additions (with --cover): level pill, progress, theme editor, self-check, welcome, what's new
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    await Task.Delay(5000);
                    void Shot(string name, FrameworkElement? what = null)
                    {
                        what ??= Root;
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(what.ActualWidth * 2), (int)Math.Ceiling(what.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(what);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    SetMode(Mode.Compact); await Task.Delay(1200);
                    ShowLevel("", L.T("Громкость"), 0.46, "volume");
                    await Task.Delay(500); Shot("1-volume");
                    ShowLevel("", L.T("Яркость"), 0.8, "brightness");
                    await Task.Delay(500); Shot("2-brightness");
                    await Task.Delay(1800);
                    _progress.Demo("explorer", 62); UpdateLive();
                    await Task.Delay(1500); Shot("3-progress");
                    _progress.Demo(null, 0);
                    _pinnedOpen = true; TabSettings.IsChecked = true; Expand();
                    ((System.Windows.Controls.RadioButton)SettingsNav.Children[0]).IsChecked = true;
                    await Task.Delay(1500); RefreshSettingsPage(); await Task.Delay(300);
                    ThemeEditorHost.BringIntoView(new Rect(0, -40, 10, 330));
                    await Task.Delay(700); Shot("4-theme-editor");
                    ((System.Windows.Controls.RadioButton)SettingsNav.Children[SettingsNav.Children.Count - 1]).IsChecked = true;
                    await Task.Delay(3500); Shot("5-self-check");
                    SetMode(Mode.Compact); _pinnedOpen = false; await Task.Delay(1000);
                    for (int step = 0; step < 3; step++)
                    {
                        ShowWelcome(step); await Task.Delay(900);
                        Shot("6-welcome-" + (step + 1), (FrameworkElement)_card!.Content);
                    }
                    ShowWhatsNew(args.ElementAtOrDefault(r + 3) ?? Updater.Current.ToString(3)); await Task.Delay(900);
                    Shot("7-whats-new", (FrameworkElement)_card!.Content);
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "activitytest")   // test: a job in another window's title, a blinking taskbar button, glass while visible to capture, the volume notice
                {
                    Settings.ReadOnly = true;
                    _settings.ShowProgress = true; _settings.MirrorNotifications = true; _settings.MirrorAllApps = true; _settings.ShowLevels = true;
                    _settings.Glass = true; _settings.CaptureHidden = false; ApplyCaptureExclusion(); ApplyGlass();
                    _progress.Finished += app => Log.Write($"activitytest: finished: {app}");
                    _titleTestProcess = "powershell";
                    await Task.Delay(2500);
                    CaptureFrost();
                    Log.Write($"activitytest: glass while visible to capture: mag works={MagCapture.Works}, frost covers={_frostCovers}, frost opacity={FrostHost.Opacity:0.0}");
                    Log.Write($"activitytest: flash fallback={FlashFallback}, shell hook={_shellHookMsg != 0}, volume device={(_volumeDeviceId.Length > 0)}");
                    if (_volumeDevice != null)
                    {
                        // a nudge of one step and straight back: does the notice come up?
                        var v = _volumeDevice.AudioEndpointVolume;
                        float was = v.MasterVolumeLevelScalar;
                        v.MasterVolumeLevelScalar = was > 0.02f ? was - 0.01f : was + 0.01f;
                        await Task.Delay(350);
                        Log.Write($"activitytest: volume notice shown={_keyKind == "volume" && _keyTimer.IsEnabled}");
                        v.MasterVolumeLevelScalar = was;
                    }
                    for (int i = 0; i < 18; i++)
                    {
                        await Task.Delay(1000);
                        Log.Write($"activitytest: job={(_progress.Current is { } j ? $"{j.App} {j.Percent}%" : "none")}, pill='{CompactTime.Text}', unread={_unread}, mode={_mode}");
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "customize")   // pictures of the hand-made layout (with --cover): size grips, the Layout page, reordered tabs, the mixer
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    await Task.Delay(5000);
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * 2), (int)Math.Ceiling(Root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    SetMode(Mode.Compact); await Task.Delay(1000);
                    EnterEditMode(); await Task.Delay(700); Shot("1-edit");
                    {
                        // pull the right grip 40 px and the bottom one 9 px, the way the mouse would
                        var from = CursorDip();
                        _testCursor = (from, new Vector(0, 0), DateTime.UtcNow);
                        _gripRight!.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                        _testCursor = (from + new Vector(40, 0), new Vector(0, 0), DateTime.UtcNow);
                        _gripRight.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(40, 0));
                        _testCursor = (from, new Vector(0, 0), DateTime.UtcNow);
                        _gripBottom!.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                        _testCursor = (from + new Vector(0, 9), new Vector(0, 0), DateTime.UtcNow);
                        _gripBottom.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 9));
                        _testCursor = null;
                        Log.Write($"customize: grips: extra={_settings.PillExtra} (expect 80), scale={_settings.IslandScale} (expect 125)");
                    }
                    _settings.PillExtra = 140; _settings.IslandScale = 120; ApplyIslandScale(); MorphTo(new Size(CompactWidth, CompactHeight));
                    await Task.Delay(1500); Shot("2-edit-bigger");
                    Log.Write($"customize: edit mode={_editMode}, pill {Island.ActualWidth:0}x{Island.ActualHeight:0} at scale {UiScale}");
                    ResetPillSize(); ExitEditMode(); await Task.Delay(800);
                    _settings.PillClock = "timedate"; _settings.PillRight = "battery"; OnTick(); UpdatePillRight();
                    await Task.Delay(900); Shot("3-pill-date-battery");
                    _settings.PillClock = "time"; _settings.PillRight = "weather";
                    await DemoCommand("layoutpage"); await Task.Delay(1800); Shot("4-layout");
                    _settings.TabOrder = ["TabHome", "TabNotes", "TabMusic", "TabTimer", "TabClip", "TabSystem"]; _settings.HiddenTabs = ["TabChat", "TabCalendar", "TabWeather"];
                    ApplyTabLayout();
                    TabSystem.IsChecked = true; await Task.Delay(2500);
                    ShowTabIcons(true, false); await Task.Delay(600); Shot("5-system-mixer-tabs");
                    Log.Write($"customize: tabs shown={string.Join(",", PageTabs.Select(t => t.Name))}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "magstress")   // test: many glass grabs through the Magnification API, the size changing every time (it crashed on a resize)
                {
                    Settings.ReadOnly = true;
                    _settings.ExpandOnHover = false; _settings.Glass = false; _settings.CaptureHidden = false; ApplyCaptureExclusion(); ApplyGlass();
                    await Task.Delay(2000);
                    var tl = Island.PointToScreen(new Point(0, 0));
                    int n = secs > 0 ? secs : 1500, ok = 0;
                    var rnd = new Random(1);
                    Log.Write($"magstress: start, {n} grabs");
                    for (int i = 0; i < n; i++)
                    {
                        int w = i % 2 == 0 ? 200 + rnd.Next(60) : 700 + rnd.Next(500), h = i % 2 == 0 ? 40 + rnd.Next(20) : 300 + rnd.Next(400);
                        var rect = new System.Drawing.Rectangle((int)tl.X - w / 2, (int)tl.Y, w, h);
                        if (MagCapture.Capture(_hwnd, rect, Math.Max(1, w / 2), Math.Max(1, h / 2), null) != null) ok++;
                        if (i % 5 == 0) await Task.Delay(1);
                        if (i % 300 == 299 || i < 2) Log.Write($"magstress: {i + 1} done, {ok} ok, asked {w}x{h}, got {MagCapture.LastSize.W}x{MagCapture.LastSize.H}");
                    }
                    Log.Write($"magstress: finished without a crash, {ok}/{n} ok, works={MagCapture.Works}");
                    {
                        // is it the right piece of the screen? the same area both ways (the island hidden from the plain grab) must match
                        _settings.CaptureHidden = true; ApplyCaptureExclusion();
                        await Task.Delay(600);
                        foreach (var (cw, ch) in new[] { (240, 44), (900, 500) })
                        {
                            var rect = new System.Drawing.Rectangle((int)tl.X - cw / 2, (int)tl.Y + 4, cw, ch);
                            var plain = CaptureService.CaptureSmall(rect, cw / 2, ch / 2, null)!;
                            var copy = (byte[])plain.Clone();
                            var mag = MagCapture.Capture(_hwnd, rect, cw / 2, ch / 2, null)!;
                            double diff = 0; for (int i = 0; i < mag.Length; i += 4) diff += Math.Abs(mag[i] - copy[i]) + Math.Abs(mag[i + 1] - copy[i + 1]) + Math.Abs(mag[i + 2] - copy[i + 2]);
                            Log.Write($"magstress: same area {cw}x{ch}: mean difference {diff / (mag.Length / 4 * 3):0.0} of 255");
                        }
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "bugfix")   // test: the game pill with "FPS in games" chosen, the FPS session starting and stopping, the Discord island while the main one opens
                {
                    Settings.ReadOnly = true;
                    _settings.ExpandOnHover = false;
                    await Task.Delay(2500);
                    foreach (var (stats, hide, full) in new[] { (true, false, true), (false, true, true), (false, false, true), (true, false, false) })
                    {
                        _settings.GameStats = stats; _settings.HideInFullscreen = hide; _testFullscreen = full;
                        WatchFullscreen();
                        Log.Write($"bugfix: FPS switch={stats}, hide switch={hide}, full-screen app={full} -> game pill={_gameMode}, hidden={_fullscreenHidden}, fps session running={_fps.Running}, denied={_fps.AccessDenied}");
                        await Task.Delay(1200);
                    }
                    _testFullscreen = null; _settings.GameStats = false; WatchFullscreen();
                    await Task.Delay(1500);

                    // the Discord island: how many times does it appear / disappear while the main island opens and folds?
                    _callProbeBusy = true;   // no real probing during the test
                    _discordInCall = true; _settings.DiscordSidePill = true;
                    await Task.Delay(1500);
                    int flips = 0; bool was = _callShown;
                    async Task Watch(int ms) { for (int i = 0; i < ms / 20; i++) { await Task.Delay(20); UpdateDiscordPill(); if (_callShown != was) { flips++; was = _callShown; if (flips <= 6) Log.Write($"bugfix:   flip -> {_callShown}: mode={_mode}, target w={_w.Target:0.0}, root={Root.ActualWidth:0.0}, pill={_callPill!.DesiredSize.Width:0.0}, scale={UiScale}"); } } }
                    Log.Write($"bugfix: call island shown while folded={_callShown}");
                    foreach (var tab in new[] { TabHome, TabSettings, TabSystem })
                    {
                        flips = 0;
                        tab.IsChecked = true; _pinnedOpen = true; Expand();
                        await Watch(1600);
                        Log.Write($"bugfix: opening {tab.Name}: call island changed {flips} time(s), shown at the end={_callShown}");
                        flips = 0;
                        _pinnedOpen = false; SetMode(Mode.Compact);
                        await Watch(1400);
                        Log.Write($"bugfix: folding back: changed {flips} time(s), shown at the end={_callShown}");
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "pulltest")   // pictures and numbers of the island reaching for the cursor (with --cover): the nearest point first, the body after it
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false; _settings.Reactive = true; _settings.LiteGraphics = false;
                    await Task.Delay(5000);
                    SetMode(Mode.Compact); await Task.Delay(1200);
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * 2), (int)Math.Ceiling(Root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    Shot("0-rest");
                    // the cursor at a spot given in island coordinates (pixels from its top-left corner)
                    void CursorAt(double ix, double iy)
                    {
                        var p = Island.PointToScreen(new Point(ix - _bodyX.Value, iy - _bodyY.Value));
                        _fakeCursor = new Native.POINT { X = (int)p.X, Y = (int)p.Y };
                    }
                    double w = Island.ActualWidth, h = Island.ActualHeight;
                    foreach (var (name, ix, iy) in new[] { ("below-right", w * 0.8, h + 34), ("right", w + 40, h / 2), ("below-left", w * 0.15, h + 26), ("above-left", -30.0, -4.0) })
                    {
                        CursorAt(ix, iy);
                        foreach (var ms in new[] { 90, 200, 450, 1300 })
                        {
                            await Task.Delay(ms == 90 ? 90 : ms == 200 ? 110 : ms == 450 ? 250 : 850);
                            CursorAt(ix, iy);
                            Log.Write($"pulltest: {name} +{ms} ms: lead=({_leadX.Value:0.0},{_leadY.Value:0.0}) body=({_bodyX.Value:0.0},{_bodyY.Value:0.0})");
                            if (ms is 200 or 1300) Shot($"{name}-{ms}");
                        }
                        _fakeCursor = new Native.POINT { X = -20000, Y = -20000 };
                        await Task.Delay(1500);
                        Log.Write($"pulltest: {name} released: lead=({_leadX.Value:0.0},{_leadY.Value:0.0}) body=({_bodyX.Value:0.0},{_bodyY.Value:0.0})");
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "charsheet")   // a picture of every character added in 1.8, in three moods (idle, music, asleep)
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    var ids = Views.CharacterView.All.Select(c => c.Id).SkipWhile(id => id != "dog").ToList();
                    const double k = 5, cellW = 30 * k + 16, cellH = 22 * k + 34;
                    var moods = new[] { Views.CharacterMood.Idle, Views.CharacterMood.Music, Views.CharacterMood.Sleep };
                    int cols = 8, rowsPerMood = (int)Math.Ceiling(ids.Count / (double)cols);
                    var canvas = new System.Windows.Controls.Canvas { Width = cols * cellW, Height = moods.Length * rowsPerMood * cellH, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)) };
                    for (int m = 0; m < moods.Length; m++)
                        for (int i = 0; i < ids.Count; i++)
                        {
                            var ch = new Views.CharacterView { Placeholder = true, Kind = ids[i], Mood = moods[m], Beat = moods[m] == Views.CharacterMood.Music ? 0.6 : 0, LookX = 0.4, LookY = 0.2, LayoutTransform = new ScaleTransform(k, k) };
                            for (int t = 0; t < 14; t++) ch.Tick(0.05 + m * 0.013);
                            double x = (i % cols) * cellW + 8, y = (m * rowsPerMood + i / cols) * cellH + 6;
                            System.Windows.Controls.Canvas.SetLeft(ch, x); System.Windows.Controls.Canvas.SetTop(ch, y);
                            canvas.Children.Add(ch);
                            var label = new TextBlock { Text = ids[i] + (m == 1 ? " ♪" : m == 2 ? " z" : ""), Foreground = Brushes.Gray, FontSize = 13 };
                            System.Windows.Controls.Canvas.SetLeft(label, x); System.Windows.Controls.Canvas.SetTop(label, y + 22 * k + 4);
                            canvas.Children.Add(label);
                        }
                    canvas.Measure(new Size(canvas.Width, canvas.Height));
                    canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
                    canvas.UpdateLayout();
                    var rtb = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(canvas);
                    CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, "characters.png"));

                    // "no character": the pill without anyone, and a Discord call that must not show anywhere
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    await Task.Delay(4000);
                    void Pill(string name)
                    {
                        var shot = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * 2), 200, 192, 192, PixelFormats.Pbgra32);
                        shot.Render(Root);
                        CaptureService.SavePng(shot, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    UseCharacter("fox"); await Task.Delay(900); Pill("pill-fox");
                    UseCharacter("none"); await Task.Delay(900); Pill("pill-none");
                    _callProbeBusy = true; _discordInCall = true;
                    _settings.ShowDiscordCall = false; _settings.DiscordSidePill = true; await Task.Delay(1200);
                    Log.Write($"charsheet: call hidden, side pill on -> island shown={_callShown}, in the pill={_liveIds.Contains("discord")}");
                    _settings.DiscordSidePill = false; await Task.Delay(1200);
                    Log.Write($"charsheet: call hidden, side pill off -> island shown={_callShown}, in the pill={_liveIds.Contains("discord")}");
                    _settings.ShowDiscordCall = true; await Task.Delay(1200);
                    Log.Write($"charsheet: call shown, side pill off -> island shown={_callShown}, in the pill={_liveIds.Contains("discord")}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "looktest")   // pictures of the look options (with --cover); without --cover: does the idle transparency come on?
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    if (_demoMedia == null)
                    {
                        Settings.ReadOnly = true;
                        _settings.IdleOpacity = 50; _settings.ExpandOnHover = false; ApplyLook();
                        await Task.Delay(3500);
                        Log.Write($"looktest: idle transparency 50%: dimmed={_idleDim}, island opacity={Island.Opacity:0.00}, hover delay timer={_expandDelay.Interval.TotalMilliseconds:0} ms");
                        _settings.IdleOpacity = 100; ApplyLook();
                        await Task.Delay(1500);
                        Log.Write($"looktest: idle transparency off: dimmed={_idleDim}, island opacity={Island.Opacity:0.00}");
                        Application.Current.Shutdown();
                        return;
                    }
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    await Task.Delay(5000);
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * 2), (int)Math.Ceiling(Root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    SetMode(Mode.Compact); await Task.Delay(1000);
                    _settings.Roundness = 0; _settings.ShowShadow = false; _settings.Clock12 = true; _settings.ClockSeconds = true; ApplyLook(); OnTick();
                    await Task.Delay(1200); Shot("1-square-noshadow-seconds");
                    _settings.Roundness = 45; _settings.ShowShadow = true; _settings.ClockSeconds = false; _settings.Clock12 = false; _settings.Font = "Georgia"; ApplyLook(); OnTick();
                    await Task.Delay(1200); Shot("2-round45-georgia");
                    TabHome.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(1800); Shot("3-open-round45-georgia");
                    _settings.Roundness = 100; _settings.Font = ""; ApplyLook();
                    await DemoCommand("layoutpage"); await Task.Delay(1800); Shot("4-layout-top");
                    SettingsScroll.ScrollToVerticalOffset(330); await Task.Delay(700); Shot("5-layout-look");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "karaoketest")   // pictures and widths of the karaoke pill as lines change (with --cover)
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false; _settings.Karaoke = true;
                    await Task.Delay(4000);
                    SetMode(Mode.Compact);
                    await DemoCommand("music");
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * 2), 180, 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    string seen = Karaoke.Text; int n = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (n < 4 && sw.Elapsed.TotalSeconds < 40)
                    {
                        await Task.Delay(15);
                        if (Karaoke.Text == seen || !Karaoke.IsVisible) continue;
                        seen = Karaoke.Text; n++;
                        var widths = new List<string>();
                        var t0 = System.Diagnostics.Stopwatch.StartNew();
                        bool early = false, mid = false;
                        while (t0.ElapsedMilliseconds < 1700)
                        {
                            await Task.Delay(20);
                            long ms = t0.ElapsedMilliseconds;
                            if (ms % 100 < 22 && ms < 900) widths.Add($"{ms}:{Island.ActualWidth:0}");
                            if (!early && ms >= 90) { early = true; Shot($"line{n}-a-090ms"); }
                            if (!mid && ms >= 700) { mid = true; Shot($"line{n}-b-700ms"); }
                        }
                        Shot($"line{n}-c-1700ms");
                        Log.Write($"karaoketest: line {n} ({seen.Length} chars): target {_w.Target:0}, width over time {string.Join(" ", widths)}");
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "voicemenu")   // test: the voice menu opens whole (5 voices, nothing cut)
                {
                    Settings.ReadOnly = true; _settings.ExpandOnHover = false;
                    await Task.Delay(2500);
                    TabChat.IsChecked = true; _pinnedOpen = true; Expand();
                    await Task.Delay(1500);
                    VoiceName_Click(this, new RoutedEventArgs());
                    await Task.Delay(700);
                    var menu = VoiceNameButton.ContextMenu ?? System.Windows.Media.VisualTreeHelper.GetParent(VoiceNameButton) as object as System.Windows.Controls.ContextMenu;
                    foreach (var w in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>())
                        if (w.RootVisual is FrameworkElement fe && fe.GetType().Name.Contains("Popup"))
                        {
                            var items = 0; double h = 0;
                            void Walk(System.Windows.DependencyObject d) { for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) { var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i); if (c is System.Windows.Controls.MenuItem mi && mi.IsVisible) { items++; h += mi.ActualHeight; } Walk(c); } }
                            Walk(fe);
                            Log.Write($"voicemenu: pop-up {fe.ActualWidth:0}x{fe.ActualHeight:0}, visible items {items}, items height {h:0}");
                        }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "magtest")   // test: does the Magnification API give the screen without the island (which stays visible to capture)?
                {
                    Settings.ReadOnly = true;
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    _settings.ExpandOnHover = false; _settings.Glass = false; _settings.CaptureHidden = false; ApplyCaptureExclusion(); ApplyGlass();
                    TabHome.IsChecked = true; _pinnedOpen = true; Expand();
                    await Task.Delay(2500);
                    var tl = Island.PointToScreen(new Point(0, 0));
                    var br = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
                    var rect = new System.Drawing.Rectangle((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));
                    int w = rect.Width / 2, h = rect.Height / 2;
                    double Mean(byte[] px) { double sum = 0; for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2]; return sum / (px.Length / 4 * 3); }
                    var plain = CaptureService.CaptureSmall(rect, w, h, null)!;
                    CaptureService.SavePng(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, plain, w * 4), System.IO.Path.Combine(dir, "mag-plain.png"));
                    Log.Write($"magtest: plain grab (island in it): mean {Mean(plain):0}");
                    for (int i = 0; i < 3; i++)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var mag = MagCapture.Capture(_hwnd, rect, w, h, null);
                        Log.Write($"magtest: mag grab #{i}: works={MagCapture.Works}, {(mag == null ? "null" : $"mean {Mean(mag):0}")}, {sw.Elapsed.TotalMilliseconds:0.0} ms");
                        if (mag != null && i == 0) CaptureService.SavePng(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, mag, w * 4), System.IO.Path.Combine(dir, "mag-without.png"));
                        await Task.Delay(300);
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "notiftest")   // test: does the notification listener see new toasts? (logs only)
                {
                    Settings.ReadOnly = true;
                    var nm = new NotificationMirror();
                    Log.Write($"notiftest: access={await nm.InitAsync()}");
                    await nm.DebugPollAsync();
                    for (int i = 0; i < 40; i++) { await Task.Delay(1000); await nm.DebugPollAsync(); }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "cover")   // pictures of the newest features for the update cover (demo data, with --cover)
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false;
                    _settings.ExpandOnHover = false;
                    await Task.Delay(7000);
                    UpdateMediaUi(); SetMode(Mode.Compact); await Task.Delay(1500);
                    Snap(dir, "compact-music");
                    ShowToast("", L.T("Caps Lock включён"), null, null, new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), ms: 6000);
                    await Task.Delay(1200); Snap(dir, "toast-caps");
                    await Task.Delay(5500);
                    ShowToast("", "EN", "English", null, null, ms: 6000);
                    await Task.Delay(1200); Snap(dir, "toast-layout");
                    await Task.Delay(5500);
                    _settings.HideInApps = ["Zoom", "Minecraft"];
                    _settings.LockPosition = true;
                    _settings.IslandScale = 100;
                    TabSettings.IsChecked = true; _pinnedOpen = true; Expand();
                    ((System.Windows.Controls.RadioButton)SettingsNav.Children[3]).IsChecked = true;
                    await Task.Delay(900);
                    RefreshSettingsPage();
                    BuildHideAppChips();
                    await Task.Delay(500);
                    HideAppChips.BringIntoView(new Rect(0, -60, 10, 330));
                    await Task.Delay(900);
                    Snap(dir, "settings-new");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "features")   // test: island size, large chat, notes, hide-in-apps (nothing is saved)
                {
                    Settings.ReadOnly = true;
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false;
                    _settings.ExpandOnHover = false;
                    await Task.Delay(5000);
                    foreach (var sc in new[] { 80, 100, 130 })
                    {
                        _settings.IslandScale = sc; ApplyIslandScale(); SetMode(Mode.Compact);
                        await Task.Delay(1200);
                        var b = Island.TransformToAncestor(Root).TransformBounds(new Rect(Island.RenderSize));
                        Log.Write($"features: scale {sc}: window {Width:0}x{Height:0}, island on screen {b.Width:0}x{b.Height:0} (layout {Island.ActualWidth:0}x{Island.ActualHeight:0})");
                        Snap(dir, $"scale-{sc}");
                    }
                    _settings.IslandScale = 100; ApplyIslandScale();
                    TabNotes.IsChecked = true; _pinnedOpen = true; Expand();
                    NewNoteBox.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 40).Select(i => $"line {i}: " + new string('x', 60)));
                    await Task.Delay(1200);
                    Log.Write($"features: notes box keeps {NewNoteBox.LineCount} lines, {NewNoteBox.Text.Length} chars, box {NewNoteBox.ActualHeight:0}px");
                    Snap(dir, "notes-long");
                    NewNoteBox.Text = "";
                    foreach (var big in new[] { false, true, false })
                    {
                        _settings.ChatLarge = big; ApplyChatSize(); Remeasure();
                        await Task.Delay(400);
                        Log.Write($"features: chat large={big}: frame {ChatFrame.Height:0}, page {ChatPage.Width:0}, window {Height:0}, screen {SystemParameters.VirtualScreenHeight:0}");
                    }
                    var fg = Native.GetForegroundWindow();
                    Native.GetWindowThreadProcessId(fg, out uint fpid);
                    string fname = ""; try { fname = System.Diagnostics.Process.GetProcessById((int)fpid).ProcessName; } catch { }
                    _settings.HideInApps = [];
                    bool before = IsHiddenApp(fg);
                    _settings.HideInApps = [fname];
                    _hiddenPid = 0;
                    Log.Write($"features: hide-in-apps fg='{fname}': empty list -> {before}, listed -> {IsHiddenApp(fg)}");
                    _pinnedOpen = false; SetMode(Mode.Compact);
                    ShowToast("", L.T("Caps Lock включён"), null, null, new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), ms: 4000);
                    await Task.Delay(900);
                    Snap(dir, "toast-caps");
                    foreach (var hide in new[] { false, true })
                    {
                        _settings.CaptureHidden = hide; ApplyCaptureExclusion(); CaptureFrost(); UpdateGlassFill();
                        GetWindowDisplayAffinity(_hwnd, out uint aff);
                        Log.Write($"features: hide from capture={hide}: affinity=0x{aff:X}, frost covers={_frostCovers}");
                    }
                    Log.Write($"features: vpn app = {ControlCenter.VpnApp()?.Name ?? "none"}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "smarttest")   // test: which tab the smart island picks, and messages waiting for a typing pause
                {
                    Settings.ReadOnly = true;
                    _settings.SmartIsland = true;
                    await Task.Delay(3000);
                    string Pick() => SmartTab()?.Name ?? "(keep current)";
                    _settings.TabHabits = new() { ["TabNotes"] = Enumerable.Repeat(4, 24).ToArray(), ["TabWeather"] = Enumerable.Repeat(1, 24).ToArray() };
                    _closedAt = DateTime.MinValue; _copiedAt = DateTime.MinValue;
                    Log.Write($"smarttest: habits (notes 4x weather) -> {Pick()}");
                    _copiedAt = DateTime.Now;
                    Log.Write($"smarttest: just copied -> {Pick()}");
                    _copiedAt = DateTime.MinValue;
                    _timer.Start();
                    Log.Write($"smarttest: timer running -> {Pick()}");
                    _timer.Reset();
                    _closedAt = DateTime.Now;
                    Log.Write($"smarttest: reopened right after closing -> {Pick()}");
                    _closedAt = DateTime.MinValue;
                    _settings.TabHabits = new();
                    Log.Write($"smarttest: no habits, nothing going on -> {Pick()}");
                    int shown = 0;
                    _typedAt = DateTime.Now;
                    WhenNotTyping(() => shown++);
                    Log.Write($"smarttest: while typing: shown={shown}, waiting={_deferred.Count}");
                    await Task.Delay(2600);
                    Log.Write($"smarttest: after the pause: shown={shown}, waiting={_deferred.Count}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "liquidtest")   // pictures of liquid glass over a made-up backdrop (stripes and a grid), never the real screen
                {
                    Settings.ReadOnly = true;
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    _frostTestBackdrop = (w, h) =>
                    {
                        var px = new byte[w * h * 4];
                        Color[] bars = [Color.FromRgb(0xFF, 0x5A, 0x5F), Color.FromRgb(0xFF, 0xB3, 0x40), Color.FromRgb(0x4C, 0xD9, 0x64), Color.FromRgb(0x2E, 0x9B, 0xFF), Color.FromRgb(0xAF, 0x52, 0xDE)];
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                var c = bars[(x / 22) % bars.Length];
                                bool grid = x % 22 < 2 || y % 14 < 2;
                                int i = (y * w + x) * 4;
                                px[i] = grid ? (byte)255 : c.B; px[i + 1] = grid ? (byte)255 : c.G; px[i + 2] = grid ? (byte)255 : c.R;
                            }
                        return px;
                    };
                    var preset = BuiltInThemes.First(t => t.Id == "liquid");
                    ApplyThemePreset(preset);
                    _settings.CaptureHidden = true; ApplyCaptureExclusion();
                    await Task.Delay(3000);
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    SetMode(Mode.Compact); await Task.Delay(2500); _frostSkips = 7; CaptureFrost(); await Task.Delay(300); Shot("liquid-compact");
                    TabMusic.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(2500); CaptureFrost(); await Task.Delay(300); Shot("liquid-open");
                    if (_liquidPixels != null && _frostBmp != null)
                        CaptureService.SavePng(BitmapSource.Create(_frostBmp.PixelWidth, _frostBmp.PixelHeight, 96, 96, PixelFormats.Bgr32, null, _liquidPixels, _frostBmp.PixelWidth * 4),
                                               System.IO.Path.Combine(dir, "liquid-bent-backdrop.png"));
                    Log.Write($"liquidtest: liquid={LiquidGlass} frost covers={_frostCovers} blur={(Frost.Effect as System.Windows.Media.Effects.BlurEffect)?.Radius}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "seasons")   // pictures of the four season themes, folded and open (with --cover)
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    await Task.Delay(6000);
                    foreach (var id in Views.SeasonView.Kinds)
                    {
                        ApplyThemePreset(BuiltInThemes.First(t => t.Id == id));
                        _pinnedOpen = false; SetMode(Mode.Compact);
                        await Task.Delay(2500); Snap(dir, $"season-{id}-compact");
                        TabMusic.IsChecked = true; _pinnedOpen = true; Expand();
                        await Task.Delay(4200); Snap(dir, $"season-{id}-open");
                    }
                    // how much the scene costs: CPU time of this process over 10 s, open, with and without it
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (var on in new[] { true, false })
                    {
                        if (!on) { _settings.Scene = ""; ApplyScene(); }
                        await Task.Delay(1000);
                        proc.Refresh(); var c0 = proc.TotalProcessorTime;
                        await Task.Delay(10000);
                        proc.Refresh();
                        Log.Write($"seasons: scene {(on ? "winter on" : "off")}: CPU {(proc.TotalProcessorTime - c0).TotalMilliseconds / 10000 / Environment.ProcessorCount * 100:0.0}% of the PC");
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "discordpill")   // pictures of the Discord call island (with --cover)
                {
                    var dir = args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath();
                    Island.IsHitTestVisible = false; _settings.ExpandOnHover = false;
                    _liveDemo = true;
                    await Task.Delay(6000);
                    void Shot(string name)
                    {
                        var rtb = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(Root);
                        CaptureService.SavePng(rtb, System.IO.Path.Combine(dir, name + ".png"));
                    }
                    SetMode(Mode.Compact);
                    _demoDiscord = true;
                    await Task.Delay(1500); Shot("call-compact");
                    ShowKeyPill("\uE84A", L.T("Caps Lock включён"), null, Color.FromRgb(0xFF, 0x9F, 0x0A));
                    await Task.Delay(400); Shot("call-and-caps");
                    TabMusic.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(1500);
                    Shot("call-open");
                    _pinnedOpen = false; SetMode(Mode.Compact); _demoDiscord = false;
                    await Task.Delay(1200); Shot("call-ended");
                    Log.Write($"discordpill: lives = {string.Join(",", _liveIds)}");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "dragtest")   // test: how far behind the cursor the dragged island is
                {
                    Settings.ReadOnly = true;
                    _settings.ExpandOnHover = false;
                    await Task.Delay(4000);
                    var home = new Point(Left, Top);
                    foreach (var spring in new[] { true, false })
                    {
                        DragOnSpring = spring;
                        var speed = new Vector(1200, 0);   // a brisk drag
                        var from = CursorDip();
                        _testCursor = (from, speed, DateTime.UtcNow);
                        _pressCursor = from; _pressWindow = new Point(Left, Top);
                        _pressed = true; _dragging = true; _posActive = true;
                        _px.Snap(Left); _py.Snap(Top);
                        _px.Stiffness = _py.Stiffness = 1500; _px.Damping = _py.Damping = 72;
                        StartRendering();
                        var lags = new List<double>();
                        EventHandler f = (_, _) =>
                        {
                            var (tx, ty) = DragTarget(CursorDip());
                            lags.Add(tx - _px.Value);
                            if (spring) { _px.Target = tx; _py.Target = ty; }   // what the mouse events did before
                        };
                        CompositionTarget.Rendering += f;
                        await Task.Delay(700);
                        CompositionTarget.Rendering -= f;
                        _testCursor = null; _pressed = false; _dragging = false;
                        var steady = lags.Skip(lags.Count / 3).ToList();
                        Log.Write($"dragtest: {(spring ? "spring (old)" : "direct (new)")}: behind the cursor by {steady.Average():0.0} px on average, at most {lags.Max():0.0} px ({steady.Average() / speed.X * 1000:0} ms at {speed.X} px/s)");
                        _px.Stiffness = _py.Stiffness = 560; _px.Damping = _py.Damping = 38;
                        MoveWindow(home.X, home.Y);
                        await Task.Delay(600);
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "perfreport")   // test: the smoothness report after a few openings
                {
                    Settings.ReadOnly = true;
                    await Task.Delay(4000);
                    for (int i = 0; i < 3; i++)
                    {
                        TabHome.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(900);
                        _pinnedOpen = false; SetMode(Mode.Compact); await Task.Delay(900);
                    }
                    Log.Write("perfreport: " + Environment.NewLine + PerfReport());
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "morphsize")   // test: does the size of the (layered) window decide how smooth it is?
                {
                    Settings.ReadOnly = true;
                    var gaps = new List<double>(); TimeSpan last = default;
                    EventHandler onFrame = (_, e2) => { var t = ((RenderingEventArgs)e2).RenderingTime; if (_rendering && last != default && t != last) gaps.Add((t - last).TotalMilliseconds); last = t; };
                    async Task Run(string name)
                    {
                        gaps.Clear();
                        CompositionTarget.Rendering += onFrame;
                        for (int i = 0; i < 4; i++)
                        {
                            TabHome.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(900);
                            _pinnedOpen = false; SetMode(Mode.Compact); await Task.Delay(900);
                        }
                        CompositionTarget.Rendering -= onFrame;
                        var g = gaps.Where(x => x < 500).ToList();
                        double avg = g.Count > 0 ? g.Average() : 0;
                        Log.Write($"morphsize: {name}: window {Width:0}x{Height:0} DIP x{Dpi:0.00}: {1000 / Math.Max(1, avg):0} fps avg, worst {(g.Count > 0 ? g.Max() : 0):0} ms, slow (>20 ms) {g.Count(x => x > 20) * 100.0 / Math.Max(1, g.Count):0}%");
                    }
                    await Task.Delay(5000);
                    PerfNoResize = true;
                    double cx = Left + Width / 2;
                    foreach (var (w, h) in new[] { (900.0, 720.0), (1800.0, 1440.0), (2700.0, 1440.0), (700.0, 520.0) })
                    {
                        Width = w; Height = h; MoveWindow(cx - w / 2, Top);
                        await Task.Delay(500);
                        await Run($"{w}x{h}");
                    }
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "morph")   // test: how smooth opening / closing is (frames per second while the island moves)
                {
                    Settings.ReadOnly = true;
                    var gaps = new List<double>(); TimeSpan last = default;
                    EventHandler onFrame = (_, e2) => { var t = ((RenderingEventArgs)e2).RenderingTime; if (_rendering && last != default && t != last) gaps.Add((t - last).TotalMilliseconds); last = t; };
                    async Task Run(string name)
                    {
                        gaps.Clear();
                        CompositionTarget.Rendering += onFrame;
                        for (int i = 0; i < 5; i++)
                        {
                            TabHome.IsChecked = true; _pinnedOpen = true; Expand(); await Task.Delay(900);
                            _pinnedOpen = false; SetMode(Mode.Compact); await Task.Delay(900);
                        }
                        CompositionTarget.Rendering -= onFrame;
                        var g = gaps.Where(x => x < 500).ToList();
                        double avg = g.Count > 0 ? g.Average() : 0;
                        Log.Write($"morph: {name}: {g.Count} frames, {1000 / Math.Max(1, avg):0} fps avg, worst {(g.Count > 0 ? g.Max() : 0):0} ms, slow (>20 ms) {g.Count(x => x > 20) * 100.0 / Math.Max(1, g.Count):0}%");
                    }
                    await Task.Delay(6000);
                    await Run("as is");
                    _settings.Glass = false; ApplyGlass(); FrostHost.Visibility = Visibility.Collapsed;
                    await Run("no frost");
                    _settings.Aura = "none"; _settings.ClaudeGlow = false; SetupAura();
                    await Run("no frost, no glow");
                    PerfNoResize = true; Height = FullWindowHeight;
                    await Run("window not resized");
                    PerfNoResize = false;
                    PerfNoChromatic = true;
                    await Run("no colour split");
                    PerfNoChromatic = false;
                    ShadowHost.Visibility = Visibility.Collapsed; ShadowHost.SetBinding(VisibilityProperty, new System.Windows.Data.Binding());
                    await Run("no shadow");
                    _settings.LiteGraphics = true; ApplyGlass();
                    await Run("lite");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "perf")   // test: what the idle island costs, switching parts off one by one (nothing is saved)
                {
                    Settings.ReadOnly = true;
                    var me = System.Diagnostics.Process.GetCurrentProcess();
                    Log.Write($"perf: render tier {System.Windows.Media.RenderCapability.Tier >> 16}, window {Width}x{Height}");
                    async Task Phase(string name)
                    {
                        await Task.Delay(2000);
                        me.Refresh(); var c0 = me.TotalProcessorTime; var t0 = DateTime.Now;
                        await Task.Delay(10000);
                        me.Refresh();
                        Log.Write($"perf: {name}: {(me.TotalProcessorTime - c0).TotalMilliseconds / (DateTime.Now - t0).TotalMilliseconds * 100:0.0}% of one core");
                    }
                    await Task.Delay(8000);
                    await Phase("everything on");
                    _settings.Aura = "none"; _settings.ClaudeGlow = false; SetupAura();
                    await Phase("aura off");
                    Views.CharacterView.PerfFreeze = true;
                    await Phase("character still");
                    _settings.Glass = false; ApplyGlass(); FrostHost.Visibility = Visibility.Collapsed;
                    await Phase("+ no frosted glass");
                    ShadowHost.Visibility = Visibility.Collapsed; ShadowHost.SetBinding(VisibilityProperty, new System.Windows.Data.Binding());
                    await Phase("+ no shadow");
                    Island.Visibility = Visibility.Hidden;
                    await Phase("+ island hidden");
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "fps-test")   // test: can we read frame events, and how many frames does the island itself show
                {
                    var ok = _fps.Start();
                    Kick(6);
                    await Task.Delay(6000);
                    var we = System.Diagnostics.Process.GetProcessesByName("wallpaper64").FirstOrDefault(); Log.Write($"fps-test: started={ok} denied={_fps.AccessDenied} own={_fps.FpsOf(Environment.ProcessId)} wallpaper={(we != null ? _fps.FpsOf(we.Id) : null)}");
                    _fps.Stop();
                    return;
                }
                if (action == "clean-test")   // test: clean desktop on for 4 s, then back
                {
                    bool was = CleanDesktop.TaskbarAutoHide, icons = CleanDesktop.IconsVisible;
                    CleanDesktop.Apply(true, was, icons);
                    await Task.Delay(4000);
                    Log.Write($"clean-test: on -> autohide={CleanDesktop.TaskbarAutoHide} icons={CleanDesktop.IconsVisible}");
                    CleanDesktop.Apply(false, was, icons);
                    await Task.Delay(1500);
                    Log.Write($"clean-test: off -> autohide={CleanDesktop.TaskbarAutoHide} icons={CleanDesktop.IconsVisible} (was {was}/{icons})");
                    return;
                }
                if (action == "update-dry")   // test: check + download + verify the newest installer, but don't run it
                {
                    try
                    {
                        var info = await Updater.CheckAsync();
                        if (info == null) { Log.Write("update-dry: up to date"); return; }
                        var path = await Updater.DownloadAsync(info, null);
                        Log.Write($"update-dry: {info.Tag} -> {path} ({new System.IO.FileInfo(path).Length} bytes, sha256 {(info.Sha256 != null ? "verified" : "not published")})");
                    }
                    catch (Exception ex) { Log.Write("update-dry failed: " + ex.Message); }
                    return;
                }
                if (action == "kick") { Kick(6); Squish(0.92); await Task.Delay(120); Unsquish(3); return; }
                if (action == "timer") { _timer.SetKind(TimerKind.Timer); _timer.SetDuration(TimeSpan.FromMinutes(secs > 0 ? secs : 5)); _timer.Start(); return; }
                RunAction(action);
                if (action == "record" && secs > 0)
                {
                    await Task.Delay(secs * 1000);
                    RunAction(action);
                }
            });
        }

        if (snap >= 0)
            _ = RunSnapshotsAsync(snap + 1 < args.Length ? args[snap + 1] : System.IO.Path.Combine(AppContext.BaseDirectory, "snapshots"));
        if (review >= 0) _ = RunReviewAsync(args.ElementAtOrDefault(review + 1) ?? "review");
        int brand = Array.IndexOf(args, "--brand");
        if (brand >= 0) _ = RunBrandAsync(args.ElementAtOrDefault(brand + 1) ?? "brand");
        if (promo >= 0)
            _ = RunPromoAsync(args.ElementAtOrDefault(promo + 1) ?? "promo", args.ElementAtOrDefault(promo + 2) ?? "ru");

        await InitMediaAsync();
        await InitWeatherAsync();
    }

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        DisablePowerThrottling();
        InitStallWatch();
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);

        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        // the frosted glass photographs the screen behind the island: without this the island would
        // photograph itself (the glass turned opaque a second after opening). Also keeps it out of screenshots.
        ApplyCaptureExclusion();
        Native.AddClipboardFormatListener(_hwnd);
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        foreach (var (id, vk, name) in new[] { (1, 0x49u, "I"), (2, 0x54u, "T"), (3, 0x53u, "S"), (4, 0x52u, "R") })
            if (!Native.RegisterHotKey(_hwnd, id, mods, vk))
                Log.Write($"Ctrl+Alt+{name} is taken by another program");
        // command bar: Alt+Space, and Ctrl+Alt+Space in case another launcher already owns Alt+Space
        if (!Native.RegisterHotKey(_hwnd, 5, Native.MOD_ALT | Native.MOD_NOREPEAT, 0x20))
            Log.Write("Alt+Space is taken by another program (use Ctrl+Alt+Space)");
        Native.RegisterHotKey(_hwnd, 6, mods, 0x20);
        RegisterOpenHotkey();
    }

    // ---------------------------------------------------------------- open / fold the island from the keyboard

    public static readonly (string Name, uint Mods, uint Vk)[] OpenHotkeys =
    [
        ("Ctrl+Alt+O", Native.MOD_CONTROL | Native.MOD_ALT, 0x4F),
        ("Ctrl+Alt+A", Native.MOD_CONTROL | Native.MOD_ALT, 0x41),
        ("Alt+Q", Native.MOD_ALT, 0x51),
        ("Ctrl+Shift+Space", Native.MOD_CONTROL | 0x4, 0x20),
    ];

    /// <summary>Registers the chosen shortcut; false when another program already owns it.</summary>
    private bool RegisterOpenHotkey()
    {
        if (_hwnd == IntPtr.Zero) return true;
        Native.UnregisterHotKey(_hwnd, 7);
        var hk = OpenHotkeys.FirstOrDefault(h => h.Name == _settings.OpenHotkey);
        if (hk.Name == null) return true;   // off
        bool ok = Native.RegisterHotKey(_hwnd, 7, hk.Mods | Native.MOD_NOREPEAT, hk.Vk);
        if (!ok) Log.Write($"{hk.Name} is taken by another program");
        return ok;
    }

    private void ToggleOpenFromKeyboard()
    {
        if (_mode == Mode.Expanded)
        {
            _pinnedOpen = false;
            SetMode(Mode.Compact);
            return;
        }
        if (_hidden) ShowIsland();
        _pinnedOpen = true;   // stays open until the shortcut, Esc or a click elsewhere
        GrowWindow();
        Expand();
        Activate();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            Dispatcher.BeginInvoke(OnClipboardChanged, DispatcherPriority.Background);
            return IntPtr.Zero;
        }
        if (_shellHookMsg != 0 && msg == _shellHookMsg)
        {
            if (wParam.ToInt32() == 0x8006) OnWindowFlash(lParam);   // HSHELL_FLASH: a taskbar button started to blink
            return IntPtr.Zero;
        }
        if (msg == Native.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case 1: ToggleHidden(); break;
                case 2: ToggleTheme(); break;
                case 3: RunAction("region"); break;
                case 4: RunAction("record"); break;
                case 5 or 6: ToggleCommandBar(); break;
                case 7: ToggleOpenFromKeyboard(); break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool _shutDown;

    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        for (int id = 1; id <= 7; id++) Native.UnregisterHotKey(_hwnd, id);
        Native.RemoveClipboardFormatListener(_hwnd);
        ShutdownChat();
        _settings.Save();
        MagCapture.MarkClean();     // a normal exit: no crash guard marker left behind
        StartupGuard.Done();
        FocusMode.End(); // never leave Windows notifications switched off
        _fps.Dispose();
        ShutdownControlCenter(); // taskbar and desktop icons come back
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_recording.IsRecording) _recording.Stop();
    }

    // ================================================================ position

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void PlaceWindow()
    {
        double cx = _settings.CenterX ?? SystemParameters.PrimaryScreenWidth / 2;
        double top = _settings.Top ?? 0;
        MoveWindow(cx - Width / 2, top);
        ClampPosition();
        ApplyDock(animate: false);
    }

    /// <summary>Switch between the floating pill and the notch attached to the top edge.</summary>
    private void ApplyDock(bool animate)
    {
        Island.Margin = new Thickness(0, IslandTopMargin, 0, 0);
        ShadowHost.Margin = Island.Margin;
        if (_settings.Docked && !_hidden) MoveWindow(Left, MonitorTop());
        if (Island.ActualWidth > 0) Island_SizeChanged(this, null!);
        UpdateShape(false);
        if (animate && _mode != Mode.Compact) MorphTo(TargetSize(_mode));
    }

    /// <summary>Jump the window (no floating) and keep the position springs in sync.</summary>
    private void MoveWindow(double left, double top)
    {
        _posActive = false;
        Left = left;
        Top = top;
        _px.Snap(left);
        _py.Snap(top);
    }

    private void ClampPosition()
    {
        double minX = SystemParameters.VirtualScreenLeft, maxX = minX + SystemParameters.VirtualScreenWidth;
        double minY = SystemParameters.VirtualScreenTop, maxY = minY + SystemParameters.VirtualScreenHeight;
        double cx = Math.Clamp(Left + Width / 2, minX + 110, maxX - 110);
        MoveWindow(cx - Width / 2, Math.Clamp(Top, minY, maxY - 80));
    }

    private void SavePosition()
    {
        _settings.CenterX = Left + Width / 2;
        _settings.Top = Top;
        _settings.Save();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => AlignIsland();

    /// <summary>Float the island back to the exact top centre of the screen it is on.</summary>
    private void AlignIsland()
    {
        if (_hidden) ShowIsland();
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + 20)));
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = (screen.Bounds.Left + screen.Bounds.Width / 2.0) / dpi.DpiScaleX;
        double top = screen.Bounds.Top / dpi.DpiScaleY;
        _settings.CenterX = screen.Primary ? null : cx;
        _settings.Top = screen.Primary ? null : top;
        _settings.Save();
        _px.Snap(Left);
        _py.Snap(Top);
        _px.Target = cx - Width / 2;
        _py.Target = _settings.Docked ? MonitorTop() : top;
        _posActive = true;
        StartRendering();
        Kick(3);
    }

    // ================================================================ frame loop

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        if (!_w.IsSettled || !_h.IsSettled) { CountAnimationFrame(dt); RecordPerf(dt); }
        _w.Step(dt);
        _h.Step(dt);
        _y.Step(dt);
        Island.Width = Math.Max(20, _w.Value);
        Island.Height = Math.Max(8, _h.Value);
        IslandShift.Y = _y.Value + _bodyY.Value;

        bool liquid = StepLiquid(dt);

        // the size has arrived: fit the window to it now, even if the outline keeps moving with the music
        if (!_windowFitted && _w.IsSettled && _h.IsSettled)
        {
            _windowFitted = true;
            ShrinkWindowToPill();
            _frostSkips = 7;   // the next capture is not skipped, even when folded
            Dispatcher.InvokeAsync(CaptureFrost, System.Windows.Threading.DispatcherPriority.Background);
        }

        if (_w.IsSettled && _h.IsSettled && _y.IsSettled && !liquid)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
            UpdateShape(false);
            ShrinkWindowToPill();
            _frostSkips = 3;          // the next frost tick refreshes even when folded
            Dispatcher.InvokeAsync(CaptureFrost, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // ---------------------------------------------------------------- window size
    // A see-through (layered) window is copied in full every time anything in it moves, so its size is
    // what the island costs. Folded, the window is only a little taller than the pill (room for the
    // shadow, the glow and the bulge); it grows to full size before the island opens.
    private const double PillRoom = 150;
    private double FullWindowHeight => Math.Ceiling(720 * Math.Max(1, UiScale));

    // ---------------------------------------------------------------- island size (Settings > Appearance)

    private double UiScale => Math.Clamp(_settings.IslandScale, 80, 130) / 100.0;

    /// <summary>Scales the whole island (and its shadow); the window grows with it, keeping its centre.</summary>
    private void ApplyIslandScale()
    {
        var t = new ScaleTransform(UiScale, UiScale);
        t.Freeze();
        Island.LayoutTransform = t;
        ShadowHost.LayoutTransform = t;
        double w = Math.Ceiling(900 * Math.Max(1, UiScale));
        if (Math.Abs(Width - w) > 1)
        {
            double cx = Left + Width / 2;
            Width = w;
            MoveWindow(cx - w / 2, Top);
        }
        GrowWindow();
        ShrinkWindowToPill();
    }

    private bool _windowFitted;

    private static bool PerfNoResize;

    // Resizing a see-through window mid-animation costs a frame or two, so: it grows to full size in one
    // step (best before anything moves: when the cursor reaches the island), and shrinks back only once
    // the island has been folded and quiet for a moment.
    private const double CompactWindowHeight = 240;
    private readonly System.Windows.Threading.DispatcherTimer _shrinkTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private void GrowWindowFor(Size size)
    {
        _windowFitted = false;
        // the frosted picture must cover the new size: take it now for that size (solid only if that fails)
        if ((Math.Abs(size.Width - _w.Target) > 1 || Math.Abs(size.Height - _h.Target) > 1) && !PrecaptureFrost(size)) SetFrostCovers(false);
        _shrinkTimer.Stop();
        double need = Math.Ceiling(IslandTopMargin + Math.Max(size.Height, Island.ActualHeight) * UiScale + PillRoom);
        if (PerfNoResize || need > Height) GrowWindow(need);
    }

    private void GrowWindow(double need = 0)
    {
        // a page taller than usual (the large chat) gets the room it needs, up to the screen
        double h = Math.Min(Math.Max(FullWindowHeight, need), SystemParameters.VirtualScreenHeight);
        if (Height < h) Height = h;
    }

    private void ShrinkWindowToPill()
    {
        if (PerfNoResize) return;
        _shrinkTimer.Stop();
        if (_mode is Mode.Compact or Mode.Toast) _shrinkTimer.Start();
    }

    private void ShrinkNow()
    {
        _shrinkTimer.Stop();
        if (_dragging || _rendering && !(_w.IsSettled && _h.IsSettled) || _mode is not (Mode.Compact or Mode.Toast) || Island.IsMouseOver) return;
        double h = Math.Max(CompactWindowHeight * UiScale, Math.Ceiling(IslandTopMargin + Math.Max(Island.Height, _h.Target) * UiScale + PillRoom));
        h = Math.Min(FullWindowHeight, h);
        if (Math.Abs(Height - h) > 1) Height = h;
    }

    private void MorphTo(Size size)
    {
        GrowWindowFor(size);
        // calm mode: near-critical damping, the size glides without bouncing
        _w.Damping = _h.Damping = Liquid ? 23 : _settings.Bouncy ? 25 : 31;   // "springy": a small overshoot
        _w.Stiffness = _h.Stiffness = _settings.FastAnimations ? 560 : 340;
        _w.Target = size.Width;
        _h.Target = size.Height;
        StartRendering();
    }

    private void Island_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double r = Radius(Island.ActualHeight);
        // docked: square at the top (flush with the screen edge), round only at the bottom
        IslandContent.Clip = _settings.Docked
            ? new RectangleGeometry(new Rect(0, -r, Island.ActualWidth, Island.ActualHeight + r), r, r)
            : new RectangleGeometry(new Rect(0, 0, Island.ActualWidth, Island.ActualHeight), r, r);
        if (!_rendering) UpdateShape(false);
    }

    // ================================================================ modes

    /// <summary>Compact pill hugs its content (live activity text, dots, weather…).</summary>
    private double CompactWidth
    {
        get
        {
            var vis = CompactView.Visibility;
            CompactView.Visibility = Visibility.Visible;
            CompactView.Measure(new Size(double.PositiveInfinity, CompactHeight));
            CompactView.Visibility = vis;
            // what the content needs, plus what the user pulled the edge out (or in) by
            // (a sung line may be long: the island goes as wide as it needs, up to what fits beside the screen centre)
            double max = Karaoke.Visibility == Visibility.Visible ? 760 : 440;
            return Math.Max(140, Math.Clamp(CompactView.DesiredSize.Width + 8, 200, max) + Math.Clamp(_settings.PillExtra, PillExtraMin, PillExtraMax));
        }
    }

    private RadioButton[] Tabs => [TabHome, TabMusic, TabWeather, TabChat, TabTimer, TabClip, TabNotes, TabCalendar, TabSystem, TabSettings];

    private FrameworkElement[] AllPages => [HomePage, MusicPage, WeatherPage, ChatPage, TimerPage, ClipPage, NotesPage, CalendarPage, SystemPage, SettingsPage];

    private FrameworkElement CurrentPage
    {
        get
        {
            int i = Array.FindIndex(Tabs, t => t.IsChecked == true);
            return AllPages[Math.Max(0, i)];
        }
    }

    private Size TargetSize(Mode mode)
    {
        switch (mode)
        {
            case Mode.Expanded:
            {
                var page = CurrentPage;
                page.Measure(new Size(page.Width, double.PositiveInfinity));
                return new Size(page.Width + 28, 10 + 32 + 10 + page.DesiredSize.Height + 14);
            }
            case Mode.Toast:
                return new Size(Math.Clamp(ToastWidth(), 260, 420), 58);
            case Mode.Claude:
            {
                ClaudeView.Measure(new Size(ClaudeView.Width, double.PositiveInfinity));
                return new Size(ClaudeView.Width + 32, ClaudeView.DesiredSize.Height + 30);
            }
            case Mode.Command:
            {
                CommandView.Measure(new Size(CommandView.Width, double.PositiveInfinity));
                return new Size(CommandView.Width + 32, CommandView.DesiredSize.Height + 26);
            }
            case Mode.Drop:
            {
                DropView.Measure(new Size(DropView.Width, double.PositiveInfinity));
                return new Size(DropView.Width + 32, DropView.DesiredSize.Height + 30);
            }
            default:
                return new Size(CompactWidth, CompactHeight);
        }
    }

    private double ToastWidth()
    {
        ToastView.Measure(new Size(double.PositiveInfinity, 58));
        return ToastView.DesiredSize.Width + 30;
    }

    private void SetMode(Mode mode)
    {
        var old = _mode;
        _mode = mode;

        var views = new Dictionary<Mode, FrameworkElement>
        {
            [Mode.Compact] = CompactView,
            [Mode.Expanded] = ExpandedView,
            [Mode.Toast] = ToastView,
            [Mode.Claude] = ClaudeView,
            [Mode.Drop] = DropView,
            [Mode.Command] = CommandView,
        };

        foreach (var (m, view) in views)
        {
            if (m == mode || view.Visibility != Visibility.Visible) continue;
            var v = view;
            Conceal(v, () => { if (_mode != m) v.Visibility = Visibility.Collapsed; });
        }

        if (old != mode)
        {
            if (mode != Mode.Compact) PlayChromatic(mode == Mode.Toast ? 5 : 8); // the "print" shimmer when it opens
            Reveal(views[mode], mode == Mode.Compact ? 0.92 : 0.95, 0, 30);
            Kick(mode == Mode.Compact ? 2.5 : 3.5);
        }
        else
        {
            views[mode].Visibility = Visibility.Visible;
            views[mode].Opacity = 1;
        }

        MorphTo(TargetSize(mode));
        UpdateEyesVisibility();
        ApplyGlass();
    }

    private void Expand()
    {
        if (_hidden || _mode is Mode.Claude or Mode.Drop or Mode.Command) return;
        _collapseDelay.Stop();
        ClearUnread();
        SetMode(Mode.Expanded);
        OnPageShown();
    }

    private void TryCollapse()
    {
        if (_mode == Mode.Drop && !_dragging)
        {
            _dropped = [];
            SetMode(Mode.Compact);
            return;
        }
        if (_pinnedOpen || _dragging || _mode != Mode.Expanded) return;
        if (IslandMenu.IsOpen) return;
        if (IsActive && IsKeyboardFocusWithin) return;              // typing (city, chat…)
        if (IsActive && TabChat.IsChecked == true) return;           // chatting
        SetMode(Mode.Compact);
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_editMode) return;   // its size is being changed: it stays folded
        _collapseDelay.Stop();
        _shrinkTimer.Stop();
        GrowWindow();   // room to open, before anything moves (resizing mid-animation would stutter)
        if (_mode == Mode.Compact && !_dragging) PrecaptureFrost(TargetSize(Mode.Expanded));   // and the glass's picture for the open size
        if (_mode == Mode.Compact && !_dragging)
        {
            Squish(1.05); // swells a little under the cursor
            Kick(1.2);
            if (_settings.ExpandOnHover) _expandDelay.Start();
        }
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandDelay.Stop();
        if (!_dragging) Unsquish(0);
        if (_mode is Mode.Expanded) _collapseDelay.Start();
        if (_mode is Mode.Drop && _dropped.Length > 0) _collapseDelay.Start();
        if (_mode == Mode.Toast && !_toastTimer.IsEnabled) _toastTimer.Start();
    }

    private int _tabIndex;

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage == null || TabSettings == null) return; // during InitializeComponent
        int index = Array.IndexOf(PageTabs.Append(TabSettings).ToArray(), sender as RadioButton);   // in the order the user sees
        double slide = index > _tabIndex ? 26 : index < _tabIndex ? -26 : 0;
        _tabIndex = Math.Max(0, index);

        foreach (var page in AllPages)
            page.Visibility = Visibility.Collapsed;

        Reveal(CurrentPage, 0.98, slide, 0);
        UpdatePageDots();
        if (_mode == Mode.Expanded)
        {
            Kick(1.5);
            MorphTo(TargetSize(Mode.Expanded));
            OnPageShown();
        }
    }

    /// <summary>Lazy work for the tab that just became visible.</summary>
    private void OnPageShown()
    {
        if (TabChat.IsChecked == true) _ = EnsureChatAsync();
        else if (TabMusic.IsChecked == true) RefreshVolume();
        else if (TabSettings.IsChecked == true) RefreshSettingsPage();
        else if (TabClip.IsChecked == true) RenderClipSection();
        else if (TabNotes.IsChecked == true) RenderNotes();
        else if (TabCalendar.IsChecked == true) RenderCalendar();
        else if (TabTimer.IsChecked == true) RenderTimer();
        else if (TabSystem.IsChecked == true) _ = RefreshSystemAsync(full: true);
        Remeasure();
    }

    /// <summary>Re-measure after content changed (e.g. Home layout).</summary>
    private void Remeasure()
    {
        if (_mode is Mode.Expanded or Mode.Claude)
            Dispatcher.BeginInvoke(() => MorphTo(TargetSize(_mode)), DispatcherPriority.Loaded);
    }

    private static void Fade(UIElement element, double to, int ms, int delayMs, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (completed != null) anim.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, anim);
    }

    // ================================================================ press, drag, swipe

    private bool _pressed, _dragging, _justDragged;
    private Point _pressCursor, _pressWindow;
    private readonly List<(DateTime T, Point P)> _samples = new();

    /// <summary>Test: the old way (the window trails the cursor on a spring while dragging).</summary>
    private bool DragOnSpring;
    /// <summary>Test: a made-up cursor moving at this speed (DIP per second) from this point, since this time.</summary>
    private (Point From, Vector Speed, DateTime Since)? _testCursor;

    /// <summary>Where the window goes for a cursor position while dragging.</summary>
    private (double X, double Y) DragTarget(Point c)
    {
        var d = c - _pressCursor;
        return (_pressWindow.X + d.X, _pressWindow.Y + (_settings.Docked ? Math.Min(0, d.Y) : d.Y));
    }

    private Point CursorDip()
    {
        if (_testCursor is { } tc) return tc.From + tc.Speed * (DateTime.UtcNow - tc.Since).TotalSeconds;
        Native.GetCursorPos(out var p);
        return new Point(p.X / Dpi, p.Y / Dpi);
    }

    private static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase or TextBoxBase or Slider or ScrollBar or InkCanvas) return true;
            if (d is FrameworkElement fe && fe.Name is "ChatHost" or "ShelfList" or "CommandResults") return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private Vector CursorVelocity()
    {
        var recent = _samples.Where(s => (DateTime.Now - s.T).TotalMilliseconds < 90).ToList();
        if (recent.Count < 2) return new Vector();
        double secs = Math.Max(0.012, (recent[^1].T - recent[0].T).TotalSeconds);
        return (recent[^1].P - recent[0].P) / secs;
    }

    private void Island_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // every touch makes a wave
        SpawnRipple(e.GetPosition(RippleLayer));
        Kick(1.4);

        // the squeeze shrinks the island towards its top: a button low in the open island would slide out
        // from under the cursor before the mouse is released, and the click would be lost. Squeeze only
        // the folded pill, and never when a button was pressed.
        bool interactive = IsInteractive(e.OriginalSource as DependencyObject);
        if (!interactive && _mode == Mode.Compact) Squish(1 - 0.045 * LiquidK);
        if (interactive) return;
        _pressed = true;
        _dragging = false;
        _pressCursor = CursorDip();
        _pressWindow = new Point(Left, Top);
        _samples.Clear();
        _samples.Add((DateTime.Now, _pressCursor));
    }

    private void Island_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != Mode.Compact) return;
        SwitchLive(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Island_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        if (_settings.LockPosition) return;   // pinned in place: no dragging, no swipe-away
        var c = CursorDip();
        var d = c - _pressCursor;
        if (!_dragging)
        {
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            _dragging = true;
            _expandDelay.Stop();
            _collapseDelay.Stop();
            Island.CaptureMouse();
            _px.Snap(Left);
            _py.Snap(Top);
            _px.Stiffness = _py.Stiffness = 1500;   // follows the hand closely, without lag or wobble
            _px.Damping = _py.Damping = 72;
            _posActive = true;
        }

        _samples.Add((DateTime.Now, c));
        if (_samples.Count > 16) _samples.RemoveAt(0);

        // the window follows the cursor (each frame, see UpdateLiquid); docked: along the top edge only
        (_px.Target, _py.Target) = DragTarget(c);

        if (Liquid)
        {
            // lean into the motion and stretch along it, like a drop of water
            var v = CursorVelocity();
            _skew.Target = Math.Clamp(-v.X * 0.007, -12, 12) * LiquidK;
            double sx = Math.Clamp(Math.Abs(v.X) * 0.00007, 0, 0.12) * LiquidK, sy = Math.Clamp(Math.Abs(v.Y) * 0.00007, 0, 0.12) * LiquidK;
            _sx.Target = 1 + sx - sy * 0.5;
            _sy.Target = 1 + sy - sx * 0.5;
            _ripple = Math.Min(6, _ripple + v.Length * 0.00035);
        }
        StartRendering();
    }

    private void Island_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            Unsquish(1.6);
            return;
        }
        _pressed = false;

        if (_dragging)
        {
            _dragging = false;
            _px.Stiffness = _py.Stiffness = 560;      // the throw and the settle stay soft
            _px.Damping = _py.Damping = 38;
            Island.ReleaseMouseCapture();
            e.Handled = true;
            _justDragged = true;
            Dispatcher.BeginInvoke(() => _justDragged = false, DispatcherPriority.Background);

            var v = CursorVelocity();
            _skew.Target = 0;
            _sx.Target = _sy.Target = 1; // ...and wobbles back into shape
            Kick(Math.Min(5, 1.5 + v.Length * 0.002));

            // upward flick, or dragged above the screen edge → hide
            double screenTop = SystemParameters.VirtualScreenTop;
            if (_py.Target + IslandTopMargin < screenTop - 12 || (v.Y < -900 && _py.Target < _pressWindow.Y - 15))
            {
                HideIsland(animate: true);
                return;
            }

            // a little momentum, then it settles where it lands
            double minX = SystemParameters.VirtualScreenLeft, maxX = minX + SystemParameters.VirtualScreenWidth;
            double cx = Math.Clamp(_px.Target + Width / 2 + v.X * 0.07, minX + 110, maxX - 110);
            _px.Target = cx - Width / 2;
            _py.Target = _settings.Docked
                ? MonitorTop()
                : Math.Clamp(_py.Target + v.Y * 0.05, screenTop, screenTop + SystemParameters.VirtualScreenHeight - 80);
            StartRendering();
            if (!Island.IsMouseOver && _mode == Mode.Expanded) _collapseDelay.Start();
            return;
        }

        Unsquish(1.6);
        // easter egg: poking a character shows one of seven emotions
        if (TryEmote(e)) return;
        // plain click on the compact pill opens it
        if (_mode == Mode.Compact && !_editMode) OpenSmart();
    }

    // ================================================================ hide / show

    private void HideIsland_Click(object sender, RoutedEventArgs e) => HideIsland(animate: true);

    private void ToggleHidden()
    {
        if (_hidden) ShowIsland(); else HideIsland(animate: true);
    }

    /// <param name="auto">Folded away by the auto-hide switch: not remembered as "hidden", so the island is back after a restart.</param>
    private void HideIsland(bool animate, bool auto = false)
    {
        if (_hidden && animate) return;
        if (_mode != Mode.Compact && _mode != Mode.Claude) SetMode(Mode.Compact);
        _hidden = true;
        UpdateAuraVisibility();
        _posActive = false;
        if (!auto)
        {
            _settings.Hidden = true;
            if (Top > SystemParameters.VirtualScreenTop + 1) _settings.Top = Top; // remember where it was
            _settings.CenterX = Left + Width / 2;
            _settings.Save();
        }

        void Finish()
        {
            Island.Visibility = Visibility.Hidden;
            MoveWindow(Left, MonitorTop());
            Handle.Visibility = Visibility.Visible;
            Reveal(Handle, 0.6, 0);
        }

        if (!animate) { Finish(); return; }

        // a drop being pulled up: stretch thin, then zip away
        if (Liquid)
        {
            _sx.Target = 0.8;
            _sy.Target = 1.25;
        }
        _y.Target = -(Island.ActualHeight + IslandTopMargin + 40);
        _y.Velocity = -900;
        Kick(4);
        StartRendering();
        Fade(Island, 0, 260, 60, () => { if (_hidden) Finish(); });
    }

    private void ShowIsland()
    {
        if (!_hidden) return;
        _hidden = false;
        UpdateAuraVisibility();
        _settings.Hidden = false;
        _settings.Save();

        Handle.Visibility = Visibility.Collapsed;
        MoveWindow(Left, _settings.Top ?? MonitorTop());
        ClampPosition();
        Island.Visibility = Visibility.Visible;
        _y.Snap(-(Island.ActualHeight + IslandTopMargin + 30));
        _y.Target = 0;
        // drops back in (with waves on: squashes on "landing" and wobbles)
        _sx.Snap(Liquid ? 0.85 : 1);
        _sy.Snap(Liquid ? 1.2 : 1);
        _sx.Target = _sy.Target = 1;
        Kick(5);
        StartRendering();
        Fade(Island, 1, 220, 0);
        HandlePill.BeginAnimation(WidthProperty, null);
    }

    private double MonitorTop()
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)((Left + Width / 2) * Dpi), (int)((Top + 20) * Dpi)));
        return screen.Bounds.Top / Dpi;
    }

    private bool _handlePressed;
    private double _handlePressY;

    private void Handle_MouseEnter(object sender, MouseEventArgs e)
    {
        var ease = new SpringEase { DampingRatio = 0.55 };
        HandlePill.BeginAnimation(WidthProperty, new DoubleAnimation(84, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(HeightProperty, new DoubleAnimation(9, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void Handle_MouseLeave(object sender, MouseEventArgs e)
    {
        _handlePressed = false;
        var ease = new SpringEase { DampingRatio = 0.6 };
        HandlePill.BeginAnimation(WidthProperty, new DoubleAnimation(56, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(HeightProperty, new DoubleAnimation(6, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(220)));
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _handlePressed = true;
        _handlePressY = CursorDip().Y;
        Handle.CaptureMouse();
        Handle.MouseLeftButtonUp += HandleUp;

        void HandleUp(object s, MouseButtonEventArgs a)
        {
            Handle.MouseLeftButtonUp -= HandleUp;
            Handle.ReleaseMouseCapture();
            if (_handlePressed) { _handlePressed = false; ShowIsland(); }
        }
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        // pull down to open
        if (_handlePressed && CursorDip().Y - _handlePressY > 14)
        {
            _handlePressed = false;
            Handle.ReleaseMouseCapture();
            ShowIsland();
        }
    }

    // ================================================================ fullscreen & topmost

    private bool _gameMode;
    private bool? _testFullscreen;   // tests: pretend a full-screen program is (or isn't) in front

    private void WatchFullscreen()
    {
        // remember where the user was typing, so clipboard items can be pasted back there
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd && !OwnsWindow(fg)) _lastExternalWindow = fg;

        // a game or a full-screen program is in front. Either switch needs to know it: "hide in full screen",
        // or "FPS and load in games" (the two exclude each other, so asking only when the first one is on
        // meant the game pill never appeared for anyone who chose FPS)
        bool fullscreenApp = (_settings.HideInFullscreen || _settings.GameStats) && _hwnd != IntPtr.Zero && (_testFullscreen ?? Native.IsFullscreenAppActive(_hwnd));
        // game mode: keep a tiny stats pill instead of hiding completely
        bool game = _demoGame || (fullscreenApp && _settings.GameStats);
        bool fullscreen = fullscreenApp && _settings.HideInFullscreen;
        if (game != _gameMode)
        {
            _gameMode = game;
            OnGameModeChanged(game);
            if (_mode == Mode.Compact) MorphTo(TargetSize(Mode.Compact));
        }
        // also hide in front of the programs chosen in Settings (exe names)
        bool fs = (fullscreen && !game) || IsHiddenApp(fg) || _demoHide;
        if (fs != _fullscreenHidden)
        {
            _fullscreenHidden = fs;
            UpdateAuraVisibility();
            if (fs)
            {
                Root.IsHitTestVisible = false;
                Fade(Root, 0, 180, 0, () => { if (_fullscreenHidden) Root.Visibility = Visibility.Hidden; });
            }
            else
            {
                Root.Visibility = Visibility.Visible;
                Root.IsHitTestVisible = true;
                Fade(Root, 1, 250, 0);
                Kick(3);
            }
        }
        if (!fs && !_dragging) Native.KeepTopmost(_hwnd);
    }

    // ================================================================ clock & eyes

    private void OnTick()
    {
        _tickCount++;
        UiStep("tick");
        var now = _demoClock ?? DateTime.Now.ToString(ClockFormat, System.Globalization.CultureInfo.InvariantCulture);
        ExpandedTime.Text = now;
        if (_recording.IsRecording)
        {
            var t = DateTime.Now - _recording.StartedAt;
            CompactTime.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
            CompactTime.Foreground = (Brush)FindResource("Danger");
        }
        else
        {
            CompactTime.Text = PillClockText(now);
            CompactTime.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        }

        UpdateProgress();
        KeepRepeating();
        WatchKeyboard();
        WatchBrightness();
        UpdateLyricLine();
        UpdateLive();
        WatchAutoHide();
        if (_tickCount % 4 == 0)
        {
            SlowTick();
            if (_tickCount % 8 == 0) _media.PollLegacy();
            if (_mode == Mode.Expanded && TabMusic.IsChecked == true && !VolumeSlider.IsMouseCaptureWithin) RefreshVolume();
        }
    }

    private void UpdateEyesVisibility()
    {
        bool playing = _lastMedia?.IsPlaying == true;
        bool showArt = playing && _lastMedia?.Art != null && !_recording.IsRecording;
        RecDot.Visibility = _recording.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        MiniArt.Visibility = showArt ? Visibility.Visible : Visibility.Collapsed;
        Eyes.Visibility = !_recording.IsRecording ? Visibility.Visible : Visibility.Collapsed; // the character stays next to the cover
        EqBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        UpdatePillRight();
    }

    private readonly List<Views.CharacterView> _previewCharacters = [];
    private Native.POINT? _fakeCursor;

    /// <summary>~30 times a second: drive the character and the pill's reaction to the cursor.</summary>
    private readonly Dictionary<Views.CharacterView, (Point Center, long Tick)> _charCenters = [];
    private long _gazeTicks;
    private DateTime _gazeAt = DateTime.Now;

    private void UpdateGaze()
    {
        if (!IsLoaded || _hidden || _fullscreenHidden) return;
        _gazeTicks++;
        Native.GetCursorPos(out var cursor);
        if (_fakeCursor is Native.POINT fake) cursor = fake;   // screenshots / tests
        CheckClickOutside(cursor);
        if (_mode == Mode.Compact && !_rendering && _gazeTicks % 3 != 0)
        {
            double dx = cursor.X - (Left + Width / 2) * Dpi, dy = cursor.Y - Top * Dpi;
            if (dx * dx + dy * dy > 420 * 420) return;
        }
        var gazeNow = DateTime.Now;
        double gazeDt = Math.Clamp((gazeNow - _gazeAt).TotalSeconds, 0.001, 0.15);   // real time since the last frame
        _gazeAt = gazeNow;
        bool playing = _lastMedia?.IsPlaying == true;
        double beat = playing ? Math.Min(1, _audio.Peak * 1.6) : 0;
        var mood = _demoSleep || (_demoMedia == null && Native.IdleSeconds() > 120) ? Views.CharacterMood.Sleep
                 : _claudeCurrent != null ? Views.CharacterMood.Think
                 : playing ? Views.CharacterMood.Music : Views.CharacterMood.Idle;

        void Drive(Views.CharacterView ch, Views.CharacterMood? moodOverride = null)
        {
            if (!ch.IsVisible) return;
            if (!_charCenters.TryGetValue(ch, out var cached) || _gazeTicks - cached.Tick > 15 || _rendering)
            {
                try { cached = (ch.PointToScreen(new Point(ch.ActualWidth / 2, ch.ActualHeight / 2)), _gazeTicks); }
                catch { return; }
                _charCenters[ch] = cached;
            }
            var c = cached.Center;
            ch.LookX = Math.Clamp((cursor.X - c.X) / 170.0, -1, 1);
            ch.LookY = Math.Clamp((cursor.Y - c.Y) / 170.0, -1, 1);
            ch.Beat = beat;
            ch.Mood = moodOverride ?? mood;
            ch.Tint = _albumColor;
            ch.Sketch = _settings.HandDrawn;
            ch.Tick(gazeDt);
        }

        if (_mode == Mode.Compact) Drive(Character);
        // the Claude Code buddy on the chat window
        bool buddy = _settings.Character == "clawd" && _mode == Mode.Expanded && TabChat.IsChecked == true;
        var buddyVis = buddy ? Visibility.Visible : Visibility.Collapsed;
        if (ChatBuddy.Visibility != buddyVis)
        {
            ChatBuddy.Visibility = buddyVis;
            if (buddy) ChatBuddy.Greet();   // pops up over the chat and says hi
        }
        if (buddy) Drive(ChatBuddy, ChatBuddyMood());

        // the same character keeps you company in the open menu (unless it already sits on the chat)
        bool menu = _mode == Mode.Expanded && !buddy && _settings.Character != "none";
        var menuVis = menu ? Visibility.Visible : Visibility.Collapsed;
        if (MenuBuddy.Visibility != menuVis)
        {
            MenuBuddy.Visibility = menuVis;
            if (menu) MenuBuddy.Greet();
        }
        if (menu)
        {
            MenuBuddy.Kind = _settings.Character;
            Drive(MenuBuddy, MenuBuddyMood());
        }
        if (_mode == Mode.Expanded && TabMusic.IsChecked == true) UpdateMusicWave(gazeDt);
        UpdateAuraMood(mood == Views.CharacterMood.Think);
        if (_mode == Mode.Expanded && TabSettings.IsChecked == true)
            foreach (var preview in _previewCharacters) Drive(preview);

        UpdateReach(cursor);
    }

    private void ScheduleBlink()
    {
        // the character blinks by itself now (CharacterView)
    }

    private void Blink()
    {
        _blink.Stop();
        EyesScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true });
        ScheduleBlink();
    }

    // ================================================================ toast

    private Action? _toastAction;

    public void ShowToast(string glyph, string title, string? text, Action? onClick = null, Brush? badge = null, int ms = 2600)
    {
        if (_mode == Mode.Claude || _hidden) return;
        ToastIcon.Text = glyph;
        ToastTitle.Text = title;
        ToastText.Text = text ?? "";
        ToastText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (badge != null) ToastBadge.Background = badge; else ToastBadge.SetResourceReference(Border.BackgroundProperty, "ChipBg");
        _toastAction = onClick;
        Character.Cheer();
        MenuBuddy.Cheer();
        PulseGlow(badge is SolidColorBrush sb ? sb.Color : Color.FromRgb(0x0A, 0x84, 0xFF));
        bool already = _mode == Mode.Toast;
        SetMode(Mode.Toast);
        if (already) { Kick(3); Squish(1.04); Dispatcher.InvokeAsync(async () => { await Task.Delay(140); Unsquish(1); }); }
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _toastTimer.Start();
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (_justDragged) return;
        var a = _toastAction;
        _toastAction = null;
        SetMode(Mode.Compact);
        a?.Invoke();
    }

    // ================================================================ theme

    private void ApplyTheme() => Theme.Apply(_settings.DarkTheme, _settings.IslandColor, _settings.GlassOpacity, _settings.AccentColor);

    private void ToggleTheme()
    {
        _settings.DarkTheme = !_settings.DarkTheme;
        _settings.Save();
        ApplyTheme();
        ApplyThemeLocal();
        ApplyGlass();
        DarkSwitch.IsChecked = _settings.DarkTheme;
        Kick(4);
    }

    private void ApplyThemeLocal()
    {
        IslandShadow.Opacity = _settings.DarkTheme ? 0.38 : 0.18;
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ToggleTheme();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenTab(TabSettings);

    private void OpenTab(RadioButton tab)
    {
        if (_hidden) ShowIsland();
        tab.IsChecked = true;
        Expand();
        Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ================================================================ tray

    private void CreateTray()
    {
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "Atoll",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add(L.T("Показать / спрятать  (Ctrl+Alt+I)"), null, (_, _) => ToggleHidden());
        _tray.ContextMenuStrip.Items.Add(L.T("Скриншот области  (Ctrl+Alt+S)"), null, (_, _) => RunAction("region"));
        _tray.ContextMenuStrip.Items.Add(L.T("Выровнять по центру"), null, (_, _) => AlignIsland());
        _tray.ContextMenuStrip.Items.Add(L.T("Сменить тему  (Ctrl+Alt+T)"), null, (_, _) => ToggleTheme());
        _tray.ContextMenuStrip.Items.Add(L.T("Настройки"), null, (_, _) => OpenTab(TabSettings));
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add(L.T("Выход"), null, (_, _) => Close());
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ToggleHidden(); };
    }
}
