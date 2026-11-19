using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DynamicIsland.Views;

/// <summary>
/// The line being sung, in the folded island. The whole line is shown (the island sizes itself to it), light
/// runs across the words as they are sung, and a new line springs up from below while the old one leaves.
/// Drawn in code and animated every frame while it is visible.
/// </summary>
public sealed class KaraokeView : FrameworkElement
{
    /// <summary>The widest a line may be; longer ones are set a little smaller, then cut with an ellipsis.</summary>
    public double MaxLineWidth { get; set; } = 620;
    /// <summary>The colour of the light's leading edge (the album's colour); null = the app accent.</summary>
    public Color? Tint { get; set; }

    private string _text = "";
    private FormattedText? _line, _old;
    private double _scale = 1, _oldScale = 1;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _changedAt = -10;
    private TimeSpan _start, _length = TimeSpan.FromSeconds(3), _position;
    private double _positionAt;
    private bool _playing;
    private bool _ticking;

    public KaraokeView()
    {
        Height = 20;
        ClipToBounds = true;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Hook();
    }

    public string Text => _text;

    /// <summary>A new line starts: its words, when it starts in the song and when the next one does.</summary>
    public void SetLine(string text, TimeSpan start, TimeSpan next)
    {
        if (text == _text) return;
        _old = _line; _oldScale = _scale;
        _text = text;
        _line = Format(text, out _scale);
        _changedAt = _clock.Elapsed.TotalSeconds;
        _start = start;
        // people finish a line before the next one begins: light it over a time that fits its length,
        // and never slower than the gap to the next line
        double natural = 1.0 + text.Length * 0.11, gap = (next - start).TotalSeconds;
        _length = TimeSpan.FromSeconds(Math.Max(0.8, gap > 0.3 ? Math.Min(gap * 0.92, natural) : natural));
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Where the song is now (called a few times a second; in between the view counts by itself).</summary>
    public void Sync(TimeSpan position, bool playing)
    {
        _position = position;
        _positionAt = _clock.Elapsed.TotalSeconds;
        _playing = playing;
    }

    private FormattedText Format(string text, out double scale)
    {
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 13, Brushes.White, dpi);
        scale = 1;
        if (f.Width > MaxLineWidth)
        {
            scale = Math.Max(0.8, MaxLineWidth / f.Width);
            f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 13 * scale, Brushes.White, dpi)
            { MaxTextWidth = MaxLineWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        }
        return f;
    }

    protected override Size MeasureOverride(Size availableSize) => new(_line == null ? 0 : Math.Ceiling(Math.Min(_line.Width, MaxLineWidth)) + 2, 20);

    private void Hook()
    {
        bool want = IsVisible;
        if (want == _ticking) return;
        _ticking = want;
        if (want) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // a still line (paused, fully lit, nothing arriving) needs no redraw
        double since = _clock.Elapsed.TotalSeconds - _changedAt;
        if (since > 0.8 && (!_playing || Progress() >= 1)) return;
        InvalidateVisual();
    }

    private double Progress()
    {
        double now = _position.TotalSeconds + (_playing ? _clock.Elapsed.TotalSeconds - _positionAt : 0);
        return Math.Clamp((now - _start.TotalSeconds) / Math.Max(0.3, _length.TotalSeconds), 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_line == null) return;
        double w = ActualWidth, h = ActualHeight;
        double t = _clock.Elapsed.TotalSeconds - _changedAt;
        var fg = (TryFindResource("Fg") as SolidColorBrush)?.Color ?? Colors.White;
        var accent = Tint ?? (TryFindResource("Accent") as SolidColorBrush)?.Color ?? fg;

        // the line that was: lifts off and fades
        if (_old != null && t < 0.22)
        {
            double k = t / 0.22, ease = 1 - (1 - k) * (1 - k);
            _old.SetForegroundBrush(new SolidColorBrush(Color.FromArgb((byte)(fg.A * 0.55 * (1 - k)), fg.R, fg.G, fg.B)));
            dc.DrawText(_old, new Point((w - _old.Width) / 2, (h - _old.Height) / 2 - 11 * ease));
        }

        // the line that is: springs up from below, light running across it as it is sung
        double p = Progress();
        const double edge = 0.05;
        var dim = Color.FromArgb((byte)(fg.A * 0.42), fg.R, fg.G, fg.B);
        Brush brush;
        if (p >= 1) brush = new SolidColorBrush(fg);
        else
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            g.GradientStops.Add(new GradientStop(fg, 0));
            g.GradientStops.Add(new GradientStop(fg, Math.Max(0, p - edge)));
            g.GradientStops.Add(new GradientStop(Blend(fg, accent, 0.75), p));
            g.GradientStops.Add(new GradientStop(dim, Math.Min(1, p + edge)));
            g.GradientStops.Add(new GradientStop(dim, 1));
            brush = g;
        }
        _line.SetForegroundBrush(brush);

        double rise = 13 * Math.Exp(-8.5 * t) * Math.Cos(13 * t);          // a soft spring: up, a touch past, settle
        double grow = 1 - 0.07 * Math.Exp(-9 * t);
        double alpha = Math.Clamp(t / 0.16, 0, 1);
        double x = (w - _line.Width) / 2, y = (h - _line.Height) / 2;
        dc.PushOpacity(alpha);
        dc.PushTransform(new ScaleTransform(grow, grow, w / 2, h / 2));
        dc.DrawText(_line, new Point(x, y + rise));
        dc.Pop();
        dc.Pop();
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
