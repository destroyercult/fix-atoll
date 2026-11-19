using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>Caps Lock and the keyboard layout: a short notice on the island when either changes.</summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);

    private bool? _capsWas;
    private int _layoutWas;

    /// <summary>~4 times a second (from OnTick).</summary>
    private void WatchKeyboard()
    {
        if (_demoMedia != null) return;
        bool caps = (GetKeyState(0x14) & 1) != 0;
        if (_capsWas != null && caps != _capsWas && _settings.ShowCapsLock)
            ShowKeyPill("", caps ? L.T("Caps Lock включён") : L.T("Caps Lock выключен"), null,
                        caps ? Color.FromRgb(0xFF, 0x9F, 0x0A) : null);
        _capsWas = caps;

        // the layout belongs to the window you type in
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        uint thread = Native.GetWindowThreadProcessId(fg, out _);
        int lang = (int)((long)GetKeyboardLayout(thread) & 0xFFFF);
        if (lang == 0) return;
        if (_layoutWas != 0 && lang != _layoutWas && _settings.ShowKeyboardLayout)
        {
            string code = "", name = "";
            try
            {
                var c = new CultureInfo(lang);
                code = c.TwoLetterISOLanguageName.ToUpperInvariant();
                name = c.Parent != null && c.Parent.NativeName.Length > 0 ? c.Parent.NativeName : c.NativeName;
                if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
            }
            catch { code = $"{lang:X4}"; }
            ShowKeyPill("", code, name, null);
        }
        _layoutWas = lang;
    }

    // ------------------------------------------------------------------ the little pill beside the island

    private Canvas? _keyLayer;
    private Border? _keyPill;
    private TextBlock? _keyIcon, _keyTitle, _keySub;
    private Border? _keyBadge;
    private Grid? _keyBar;
    private Border? _keyBarFill;
    private string _keyKind = "";
    private readonly System.Windows.Threading.DispatcherTimer _keyTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };

    /// <summary>
    /// Caps Lock / layout: a small pill next to the island (below it when the island is open) instead of
    /// a notice on the island itself, so whatever the island shows keeps working and nothing folds.
    /// </summary>
    private void ShowKeyPill(string glyph, string title, string? sub, Color? accent, double? level = null, string kind = "")
    {
        if (_hidden || _fullscreenHidden || !IsLoaded) return;
        if (_keyLayer == null)
        {
            _keyLayer = new Canvas { IsHitTestVisible = false };
            Root.Children.Add(_keyLayer);
            _keyIcon = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _keyBadge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Child = _keyIcon, Margin = new Thickness(0, 0, 8, 0) };
            _keyTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            _keySub = new TextBlock { FontSize = 12, Opacity = 0.65, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _keyTitle.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            _keySub.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            _keyIcon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            // a level (volume, brightness): a thin bar between the name and the number
            _keyBarFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            _keyBarFill.SetResourceReference(Border.BackgroundProperty, "Fg");
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Opacity = 0.22 };
            track.SetResourceReference(Border.BackgroundProperty, "Fg");
            _keyBar = new Grid { Width = 78, Height = 4, Margin = new Thickness(9, 1, 2, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _keyBar.Children.Add(track); _keyBar.Children.Add(_keyBarFill);
            row.Children.Add(_keyBadge); row.Children.Add(_keyTitle); row.Children.Add(_keyBar); row.Children.Add(_keySub);
            _keyPill = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(5, 5, 14, 5), Child = row, Opacity = 0,
                                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.8, 0.8) };
            _keyPill.SetResourceReference(Border.BackgroundProperty, "IslandBg");
            _keyPill.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
            _keyPill.BorderThickness = new Thickness(1);
            _keyLayer.Children.Add(_keyPill);
            _keyTimer.Tick += (_, _) =>
            {
                _keyTimer.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                _keyPill!.BeginAnimation(OpacityProperty, fade);
            };
        }
        _keyIcon!.Text = glyph;
        _keyTitle!.Text = title;
        _keySub!.Text = sub ?? "";
        _keySub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        _keySub.MinWidth = level != null ? 34 : 0;   // the number changes while the key is held: the pill must not jitter
        _keyBar!.Visibility = level != null ? Visibility.Visible : Visibility.Collapsed;
        if (level is double lv) _keyBarFill!.Width = 78 * Math.Clamp(lv, 0, 1);
        // the same kind of notice again while it is still up (a held volume key): just update it, no new pop
        bool again = kind.Length > 0 && kind == _keyKind && _keyTimer.IsEnabled;
        _keyKind = kind;
        if (accent is Color a) { _keyBadge!.Background = new SolidColorBrush(a); _keyBadge.Background.Freeze(); }
        else _keyBadge!.SetResourceReference(Border.BackgroundProperty, "ChipBg");

        // where: to the right of the folded pill, under the open island
        _keyPill!.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _keyPill.DesiredSize;
        var islandTl = Island.TranslatePoint(new Point(0, 0), Root);
        double iw = Island.ActualWidth * UiScale, ih = Island.ActualHeight * UiScale;
        double x, y;
        bool side = _mode != Mode.Expanded && islandTl.X + iw + 12 + SideTaken + size.Width <= Root.ActualWidth;
        if (side) { x = islandTl.X + iw + 10 + SideTaken; y = islandTl.Y + Math.Max(0, (Math.Min(ih, 44) - size.Height) / 2); }
        else { x = islandTl.X + (iw - size.Width) / 2; y = islandTl.Y + ih + 10; }
        if (y + size.Height > Root.ActualHeight) GrowWindow(y + size.Height + 4);
        Canvas.SetLeft(_keyPill, Math.Max(0, x));
        Canvas.SetTop(_keyPill, y);

        _keyTimer.Stop();
        _keyTimer.Start();
        if (again) { _keyPill.BeginAnimation(OpacityProperty, null); _keyPill.Opacity = 1; return; }
        _keyPill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        var pop = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        ((ScaleTransform)_keyPill.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ((ScaleTransform)_keyPill.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        _keyTimer.Stop();
        _keyTimer.Start();
    }
}
