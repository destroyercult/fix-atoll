using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Views;

/// <summary>
/// More characters (1.8): animals, little things and weather. Drawn like the first ones — in code, around
/// the centre of a 30 × 22 box, with eyes that follow the cursor and the same moods (music, happy, think, sleep).
/// </summary>
public sealed partial class CharacterView
{
    private static Brush Rgb(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));

    private static readonly Brush Ink = Rgb(0x1B, 0x1B, 0x1F), Paper = Rgb(0xF6, 0xF6, 0xF8), Pink = Rgb(0xFF, 0x9F, 0xB5),
        Orange = Rgb(0xFF, 0x8A, 0x3D), Yellow = Rgb(0xFF, 0xD2, 0x4A), Green = Rgb(0x5B, 0xC2, 0x6A), Blue = Rgb(0x5A, 0xC8, 0xFA);
    private static readonly Pen InkThin = FrozenPen(Ink, 0.7), InkLine = FrozenPen(Ink, 1.1);

    private static Pen FrozenPen(Brush b, double t)
    {
        var p = new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static Geometry Poly(params double[] xy)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(xy[0], xy[1]), true, true);
            for (int i = 2; i < xy.Length; i += 2) c.LineTo(new Point(xy[i], xy[i + 1]), true, true);
        }
        return g;
    }

    /// <summary>Two small dark eyes on a coloured face.</summary>
    private void DarkEyes(DrawingContext dc, double cx, double y, double apart, double rx, double ry, CharacterMood mood)
    {
        Eye(dc, cx - apart, y, rx, ry, mood, Ink, Brushes.White, small: true);
        Eye(dc, cx + apart, y, rx, ry, mood, Ink, Brushes.White, small: true);
    }

    private bool DrawMore(DrawingContext dc, double cx, double cy, double y, CharacterMood mood)
    {
        switch (Kind)
        {
            case "dog": DrawDog(dc, cx, y, mood); return true;
            case "fox": DrawFox(dc, cx, y, mood); return true;
            case "panda": DrawPanda(dc, cx, y, mood); return true;
            case "bear": DrawBear(dc, cx, y, mood); return true;
            case "frog": DrawFrog(dc, cx, y, mood); return true;
            case "penguin": DrawPenguin(dc, cx, y, mood); return true;
            case "chick": DrawChick(dc, cx, y, mood); return true;
            case "pig": DrawPig(dc, cx, y, mood); return true;
            case "koala": DrawKoala(dc, cx, y, mood); return true;
            case "mouse": DrawMouse(dc, cx, y, mood); return true;
            case "bee": DrawBee(dc, cx, y, mood); return true;
            case "octopus": DrawOctopus(dc, cx, y, mood); return true;
            case "mushroom": DrawMushroom(dc, cx, y, mood); return true;
            case "cactus": DrawCactus(dc, cx, y, mood); return true;
            case "alien": DrawAlien(dc, cx, y, mood); return true;
            case "bat": DrawBat(dc, cx, y, mood); return true;
            case "moon": DrawMoon(dc, cx, y, mood); return true;
            case "sun": DrawSun(dc, cx, y, mood); return true;
            case "flame": DrawFlame(dc, cx, y, mood); return true;
            case "drop": DrawDrop(dc, cx, y, mood); return true;
            case "heart": DrawHeart(dc, cx, y, mood); return true;
            case "pumpkin": DrawPumpkin(dc, cx, y, mood); return true;
            case "none":
                if (!Placeholder) return true;
                // in the gallery: an empty spot, so there is something to click
                var dash = new Pen(_fg, 1.2) { DashStyle = new DashStyle([2, 2], 0) };
                dc.PushOpacity(0.55);
                dc.DrawEllipse(null, dash, new Point(cx, cy), 8, 8);
                dc.DrawLine(new Pen(_fg, 1.2), new Point(cx - 5.2, cy + 5.2), new Point(cx + 5.2, cy - 5.2));
                dc.Pop();
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ animals

    private static readonly Brush DogFur = Rgb(0xE8, 0xB8, 0x7A), DogEar = Rgb(0x9A, 0x62, 0x38);

    private void DrawDog(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // floppy ears swing with the music and hang lower asleep
        double flop = mood == CharacterMood.Music ? Math.Sin(_t * 7) * (8 + _beat * 16) : Math.Sin(_t * 1.5) * 3;
        double hang = mood == CharacterMood.Sleep ? 10 : 0;
        foreach (var side in new[] { -1, 1 })
        {
            dc.PushTransform(new RotateTransform(side * (16 + hang) + flop * side, cx + side * 7, y - 5));
            dc.DrawEllipse(DogEar, null, new Point(cx + side * 8.6, y - 0.5), 3.1, 6.2);
            dc.Pop();
        }
        dc.DrawEllipse(DogFur, null, new Point(cx, y + 1), 9.4, 7.6);
        dc.DrawEllipse(Paper, null, new Point(cx, y + 4), 4.6, 3.2);
        DarkEyes(dc, cx, y - 0.8, 4.1, 2.1, 2.5, mood);
        dc.DrawEllipse(Ink, null, new Point(cx, y + 2.7), 1.5, 1.05);
        if (mood is CharacterMood.Happy or CharacterMood.Music)
            dc.DrawEllipse(Pink, null, new Point(cx, y + 6 + _beat), 1.5, 1.7 + _beat * 1.2);   // tongue out
    }

    private static readonly Brush FoxTip = Rgb(0x5A, 0x34, 0x22);

    private void DrawFox(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        double twitch = Math.Sin(_t * 1.1 + 2) > 0.96 ? 1.3 : 0;
        dc.DrawGeometry(Orange, null, Poly(cx - 10, y - 1, cx - 9, y - 10.5 - twitch, cx - 2.5, y - 5.5));
        dc.DrawGeometry(Orange, null, Poly(cx + 10, y - 1, cx + 9, y - 10.5, cx + 2.5, y - 5.5));
        dc.DrawGeometry(FoxTip, null, Poly(cx - 9, y - 10.5 - twitch, cx - 9.6, y - 6.5, cx - 6.4, y - 8.4));
        dc.DrawGeometry(FoxTip, null, Poly(cx + 9, y - 10.5, cx + 9.6, y - 6.5, cx + 6.4, y - 8.4));
        dc.DrawEllipse(Orange, null, new Point(cx, y + 1), 10, 7.4);
        // white cheeks meeting at the nose
        dc.PushClip(new EllipseGeometry(new Point(cx, y + 1), 10, 7.4));
        dc.DrawEllipse(Paper, null, new Point(cx - 5.2, y + 6), 5.6, 4.6);
        dc.DrawEllipse(Paper, null, new Point(cx + 5.2, y + 6), 5.6, 4.6);
        dc.Pop();
        DarkEyes(dc, cx, y - 0.4, 4.2, 2.1, 2.5, mood);
        dc.DrawEllipse(Ink, null, new Point(cx, y + 4.2), 1.25, 0.95);
    }

    private void DrawPanda(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        dc.DrawEllipse(Ink, null, new Point(cx - 7.2, y - 6), 3.4, 3.4);
        dc.DrawEllipse(Ink, null, new Point(cx + 7.2, y - 6), 3.4, 3.4);
        dc.DrawEllipse(Paper, InkThin, new Point(cx, y + 1), 9.6, 7.8);
        foreach (var side in new[] { -1, 1 })
        {
            dc.PushTransform(new RotateTransform(side * 22, cx + side * 4.2, y));
            dc.DrawEllipse(Ink, null, new Point(cx + side * 4.2, y), 2.9, 3.6);
            dc.Pop();
            Eye(dc, cx + side * 4.1, y - 0.2, 1.6, 1.9, mood, Brushes.White, Ink, small: true);
        }
        dc.DrawEllipse(Ink, null, new Point(cx, y + 3.4), 1.3, 0.9);
        if (mood == CharacterMood.Music) dc.DrawEllipse(Ink, null, new Point(cx, y + 5.6), 1.1, 0.5 + _beat * 1.4);
    }

    private static readonly Brush BearFur = Rgb(0xA9, 0x71, 0x4B), BearLight = Rgb(0xE6, 0xC3, 0xA1);

    private void DrawBear(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        foreach (var side in new[] { -1, 1 })
        {
            dc.DrawEllipse(BearFur, null, new Point(cx + side * 7.2, y - 5.8), 3.4, 3.4);
            dc.DrawEllipse(BearLight, null, new Point(cx + side * 7.2, y - 5.6), 1.7, 1.7);
        }
        dc.DrawEllipse(BearFur, null, new Point(cx, y + 1), 9.6, 7.8);
        dc.DrawEllipse(BearLight, null, new Point(cx, y + 3.8), 4.2, 3.1);
        DarkEyes(dc, cx, y - 0.8, 4.1, 2, 2.4, mood);
        dc.DrawEllipse(Ink, null, new Point(cx, y + 2.7), 1.5, 1.05);
        Mouth(dc, cx, y + 5.1, mood, 1.3, Ink);
    }

    private static readonly Brush FrogDark = Rgb(0x3E, 0x9E, 0x4E);

    private void DrawFrog(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // the throat swells with the music
        double puff = mood == CharacterMood.Music ? _beat * 1.6 : Math.Max(0, Math.Sin(_t * 2.2)) * 0.5;
        dc.DrawEllipse(Green, null, new Point(cx - 5.2, y - 4.4), 3.8, 3.8);
        dc.DrawEllipse(Green, null, new Point(cx + 5.2, y - 4.4), 3.8, 3.8);
        dc.DrawEllipse(Green, null, new Point(cx, y + 2), 10, 6.6 + puff);
        Eye(dc, cx - 5.2, y - 4.6, 2.7, 2.9, mood, Brushes.White, Ink, small: true);
        Eye(dc, cx + 5.2, y - 4.6, 2.7, 2.9, mood, Brushes.White, Ink, small: true);
        dc.DrawGeometry(null, FrozenPen(FrogDark, 1.1), Arc(cx - 5.5, y + 2.4, cx + 5.5, y + 2.4, 1.6));
        Blush(dc, cx - 7, y + 3, 1.6);
        Blush(dc, cx + 7, y + 3, 1.6);
    }

    private static readonly Brush PenguinBody = Rgb(0x2E, 0x38, 0x52);

    private void DrawPenguin(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // flippers wave with the music
        double flap = mood == CharacterMood.Music ? Math.Sin(_t * 9) * (14 + _beat * 26) : mood == CharacterMood.Happy ? Math.Sin(_t * 14) * 22 : 0;
        foreach (var side in new[] { -1, 1 })
        {
            dc.PushTransform(new RotateTransform(side * (24 + flap), cx + side * 7, y - 1));
            dc.DrawEllipse(PenguinBody, null, new Point(cx + side * 8, y + 2.4), 2.1, 4.8);
            dc.Pop();
            dc.DrawEllipse(Orange, null, new Point(cx + side * 3.2, y + 9.2), 2.5, 1.2);
        }
        dc.DrawEllipse(PenguinBody, null, new Point(cx, y + 0.4), 8.4, 9);
        dc.DrawEllipse(Paper, null, new Point(cx, y + 2.6), 5.7, 6.1);
        Eye(dc, cx - 2.9, y - 2.4, 1.8, 2.1, mood, Ink, Brushes.White, small: true);
        Eye(dc, cx + 2.9, y - 2.4, 1.8, 2.1, mood, Ink, Brushes.White, small: true);
        dc.DrawGeometry(Orange, null, Poly(cx - 1.7, y + 0.2, cx + 1.7, y + 0.2, cx, y + 2.6));
    }

    private void DrawChick(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        double flap = mood == CharacterMood.Music ? Math.Sin(_t * 12) * (12 + _beat * 24) : mood == CharacterMood.Happy ? Math.Sin(_t * 16) * 20 : 0;
        foreach (var side in new[] { -1, 1 })
        {
            dc.PushTransform(new RotateTransform(side * (30 + flap), cx + side * 7, y + 1));
            dc.DrawEllipse(Orange, null, new Point(cx + side * 8.6, y + 2), 2.2, 3.6);
            dc.Pop();
        }
        // a tuft of three feathers
        var tuft = FrozenPen(Yellow, 1.5);
        dc.DrawLine(tuft, new Point(cx, y - 7), new Point(cx, y - 10.4));
        dc.DrawLine(tuft, new Point(cx - 1.4, y - 7), new Point(cx - 2.8, y - 9.6));
        dc.DrawLine(tuft, new Point(cx + 1.4, y - 7), new Point(cx + 2.8, y - 9.6));
        dc.DrawEllipse(Yellow, null, new Point(cx, y + 1), 8.4, 8.2);
        DarkEyes(dc, cx, y - 0.8, 3.5, 1.9, 2.3, mood);
        double open = mood == CharacterMood.Music ? 0.6 + _beat * 1.6 : 0;
        dc.DrawGeometry(Orange, null, Poly(cx - 2, y + 2, cx + 2, y + 2, cx, y + 4 + open));
        Blush(dc, cx - 5.6, y + 3, 1.5);
        Blush(dc, cx + 5.6, y + 3, 1.5);
    }

    private static readonly Brush PigSkin = Rgb(0xFF, 0xB0, 0xC2), PigSnout = Rgb(0xF2, 0x7F, 0x9C);

    private void DrawPig(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        dc.DrawGeometry(PigSnout, null, Poly(cx - 9.5, y - 2, cx - 8, y - 9.5, cx - 3, y - 5.5));
        dc.DrawGeometry(PigSnout, null, Poly(cx + 9.5, y - 2, cx + 8, y - 9.5, cx + 3, y - 5.5));
        dc.DrawEllipse(PigSkin, null, new Point(cx, y + 1), 9.8, 7.6);
        double sniff = Math.Sin(_t * 3) * 0.25;
        dc.DrawEllipse(PigSnout, null, new Point(cx, y + 3 + sniff), 4.2, 2.9);
        dc.DrawEllipse(Ink, null, new Point(cx - 1.5, y + 3 + sniff), 0.75, 1.05);
        dc.DrawEllipse(Ink, null, new Point(cx + 1.5, y + 3 + sniff), 0.75, 1.05);
        DarkEyes(dc, cx, y - 1.4, 4.4, 1.9, 2.3, mood);
    }

    private static readonly Brush KoalaFur = Rgb(0xA7, 0xAE, 0xBA), KoalaEar = Rgb(0xE3, 0xE6, 0xEC);

    private void DrawKoala(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        foreach (var side in new[] { -1, 1 })
        {
            dc.DrawEllipse(KoalaFur, null, new Point(cx + side * 8.6, y - 3.2), 4.7, 4.7);
            dc.DrawEllipse(KoalaEar, null, new Point(cx + side * 8.8, y - 3), 2.6, 2.6);
        }
        dc.DrawEllipse(KoalaFur, null, new Point(cx, y + 1), 8.6, 7.6);
        DarkEyes(dc, cx, y - 0.6, 4.4, 1.7, 2.1, mood);
        dc.DrawEllipse(Ink, null, new Point(cx, y + 2), 2, 2.8);
    }

    private static readonly Brush MouseFur = Rgb(0xC9, 0xCD, 0xD6);

    private void DrawMouse(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        double wiggle = Math.Sin(_t * 9) * 0.3;
        foreach (var side in new[] { -1, 1 })
        {
            dc.DrawEllipse(MouseFur, null, new Point(cx + side * 7.2, y - 4.8), 5, 5);
            dc.DrawEllipse(Pink, null, new Point(cx + side * 7.2, y - 4.6), 3, 3);
        }
        dc.DrawEllipse(MouseFur, null, new Point(cx, y + 2), 8.2, 6.8);
        DarkEyes(dc, cx, y + 0.6, 3.6, 1.9, 2.3, mood);
        dc.DrawEllipse(Pink, null, new Point(cx, y + 4.4 + wiggle), 1.1, 0.85);
        dc.DrawLine(InkThin, new Point(cx - 8.6, y + 3.6), new Point(cx - 4.6, y + 4.4 + wiggle));
        dc.DrawLine(InkThin, new Point(cx + 8.6, y + 3.6), new Point(cx + 4.6, y + 4.4 + wiggle));
    }

    private void DrawBee(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        y += Math.Sin(_t * 5) * 1.1;   // hovers
        // wings: a fast blur, slower asleep
        double beat = mood == CharacterMood.Sleep ? 0.15 : Math.Abs(Math.Sin(_t * 38));
        dc.PushOpacity(0.75);
        foreach (var side in new[] { -1, 1 })
        {
            dc.PushTransform(new RotateTransform(side * (24 + beat * 20), cx + side * 2, y - 4));
            dc.DrawEllipse(Paper, null, new Point(cx + side * 4.6, y - 8), 3.1, 4.6);
            dc.Pop();
        }
        dc.Pop();
        var body = new EllipseGeometry(new Point(cx, y + 1.8), 8.8, 6.4);
        dc.DrawGeometry(Yellow, null, body);
        dc.PushClip(body);
        dc.DrawRectangle(Ink, null, new Rect(cx + 1.6, y - 6, 2.4, 16));
        dc.DrawRectangle(Ink, null, new Rect(cx + 5.6, y - 6, 2.4, 16));
        dc.Pop();
        DarkEyes(dc, cx - 3.2, y + 1.4, 2.5, 1.7, 2.1, mood);
        dc.DrawLine(InkThin, new Point(cx - 4.6, y - 3.8), new Point(cx - 6.2, y - 7.4));
        dc.DrawLine(InkThin, new Point(cx - 1.8, y - 4.4), new Point(cx - 1.4, y - 8));
        dc.DrawEllipse(Ink, null, new Point(cx - 6.2, y - 7.6), 0.8, 0.8);
        dc.DrawEllipse(Ink, null, new Point(cx - 1.4, y - 8.2), 0.8, 0.8);
    }

    private static readonly Brush OctoBody = Rgb(0xB0, 0x7C, 0xF5);

    private void DrawOctopus(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // four arms that never stop curling
        var arm = FrozenPen(OctoBody, 2.7);
        double speed = mood == CharacterMood.Music ? 8 : mood == CharacterMood.Sleep ? 1.2 : 3.2;
        for (int i = 0; i < 4; i++)
        {
            double x = cx - 6 + i * 4, sway = Math.Sin(_t * speed + i * 1.4) * (1.6 + _beat * 2);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, y + 2), false, false);
                c.QuadraticBezierTo(new Point(x + sway, y + 6), new Point(x - sway * 0.7, y + 9.4), true, true);
            }
            dc.DrawGeometry(null, arm, g);
        }
        dc.DrawEllipse(OctoBody, null, new Point(cx, y - 1.4), 8.4, 7.2);
        Eye(dc, cx - 3.3, y - 1.4, 2.2, 2.6, mood, Brushes.White, Ink, small: true);
        Eye(dc, cx + 3.3, y - 1.4, 2.2, 2.6, mood, Brushes.White, Ink, small: true);
        Blush(dc, cx - 5.8, y + 1.4, 1.4);
        Blush(dc, cx + 5.8, y + 1.4, 1.4);
    }

    // ------------------------------------------------------------------ little things

    private static readonly Brush CapRed = Rgb(0xFF, 0x5A, 0x5F), Stem = Rgb(0xFF, 0xF1, 0xDC);

    private void DrawMushroom(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        dc.DrawEllipse(Stem, null, new Point(cx, y + 4.6), 5.4, 5);
        double tilt = mood == CharacterMood.Music ? Math.Sin(_t * 7) * (3 + _beat * 6) : Math.Sin(_t * 1.6) * 1.5;
        dc.PushTransform(new RotateTransform(tilt, cx, y + 2));
        var cap = new StreamGeometry();
        using (var c = cap.Open())
        {
            c.BeginFigure(new Point(cx - 10.6, y + 1.6), true, true);
            c.ArcTo(new Point(cx + 10.6, y + 1.6), new Size(10.6, 10.4), 0, false, SweepDirection.Clockwise, true, true);
            c.QuadraticBezierTo(new Point(cx, y + 4), new Point(cx - 10.6, y + 1.6), true, true);
        }
        dc.DrawGeometry(CapRed, null, cap);
        dc.DrawEllipse(Paper, null, new Point(cx - 5.4, y - 3), 1.9, 1.7);
        dc.DrawEllipse(Paper, null, new Point(cx + 4.4, y - 4.4), 2.2, 1.9);
        dc.DrawEllipse(Paper, null, new Point(cx - 0.4, y - 7), 1.4, 1.2);
        dc.DrawEllipse(Paper, null, new Point(cx + 7.6, y - 0.6), 1.2, 1.1);
        dc.Pop();
        DarkEyes(dc, cx, y + 5, 2.3, 1.4, 1.8, mood);
    }

    private static readonly Brush CactusGreen = Rgb(0x4C, 0xB8, 0x6B);

    private void DrawCactus(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // arms lift with the music
        double lift = mood == CharacterMood.Music ? _beat * 3 + Math.Abs(Math.Sin(_t * 7)) * 1.5 : mood == CharacterMood.Happy ? 2 : 0;
        var arm = FrozenPen(CactusGreen, 3.5);
        var left = new StreamGeometry();
        using (var c = left.Open())
        {
            c.BeginFigure(new Point(cx - 4, y + 3.4), false, false);
            c.LineTo(new Point(cx - 8.6, y + 3.4), true, true);
            c.LineTo(new Point(cx - 8.6, y - 1 - lift), true, true);
            c.BeginFigure(new Point(cx + 4, y + 0.6), false, false);
            c.LineTo(new Point(cx + 8.6, y + 0.6), true, true);
            c.LineTo(new Point(cx + 8.6, y - 4 - lift), true, true);
        }
        dc.DrawGeometry(null, arm, left);
        dc.DrawRoundedRectangle(CactusGreen, null, new Rect(cx - 4.8, y - 8.6, 9.6, 18), 4.8, 4.8);
        // a flower on top
        dc.DrawEllipse(Pink, null, new Point(cx + 1.6, y - 9), 2, 1.7);
        dc.DrawEllipse(Yellow, null, new Point(cx + 1.6, y - 9), 0.8, 0.8);
        DarkEyes(dc, cx, y - 1.6, 2.1, 1.4, 1.8, mood);
        Mouth(dc, cx, y + 1.9, mood, 1.1, Ink);
    }

    private static readonly Brush AlienSkin = Rgb(0x7B, 0xE0, 0x8A), AlienGlow = Rgb(0xD6, 0xFF, 0xDC);

    private void DrawAlien(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        y += Math.Sin(_t * 2) * 0.8;
        // antenna with a bobbing light
        double sway = Math.Sin(_t * 3) * 1.4 + (mood == CharacterMood.Music ? Math.Sin(_t * 9) * _beat * 3 : 0);
        dc.DrawLine(FrozenPen(AlienSkin, 1.2), new Point(cx, y - 7), new Point(cx + sway, y - 10.6));
        dc.DrawEllipse(mood == CharacterMood.Think ? Yellow : AlienGlow, null, new Point(cx + sway, y - 10.8), 1.3, 1.3);
        // a head wide at the top, narrow at the chin
        var head = new StreamGeometry();
        using (var c = head.Open())
        {
            c.BeginFigure(new Point(cx, y - 8), true, true);
            c.BezierTo(new Point(cx + 11.4, y - 8), new Point(cx + 9, y + 5.6), new Point(cx, y + 8.6), true, true);
            c.BezierTo(new Point(cx - 9, y + 5.6), new Point(cx - 11.4, y - 8), new Point(cx, y - 8), true, true);
        }
        dc.DrawGeometry(AlienSkin, null, head);
        foreach (var side in new[] { -1, 1 })
        {
            dc.PushTransform(new RotateTransform(side * -18, cx + side * 3.8, y - 0.8));
            Eye(dc, cx + side * 3.8, y - 0.8, 2.7, 3.5, mood, Ink, AlienGlow, small: true);
            dc.Pop();
        }
        if (mood != CharacterMood.Sleep) dc.DrawEllipse(Ink, null, new Point(cx, y + 5.2), 0.9, 0.5 + (mood == CharacterMood.Music ? _beat * 1.2 : 0));
    }

    private static readonly Brush BatBody = Rgb(0x6B, 0x57, 0xA8);

    private void DrawBat(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        double flap = mood == CharacterMood.Sleep ? -0.6 : Math.Sin(_t * (mood == CharacterMood.Music ? 14 : 8));
        y += flap * 0.9;
        foreach (var side in new[] { -1.0, 1.0 })
        {
            dc.DrawGeometry(BatBody, null, Poly(
                cx + side * 4, y - 1,
                cx + side * 13, y - 4 - flap * 4.5,
                cx + side * 10.6, y + 0.8,
                cx + side * 12.4, y + 3.6 - flap * 2,
                cx + side * 8, y + 2.6,
                cx + side * 7.6, y + 5.6 - flap,
                cx + side * 4, y + 4));
        }
        dc.DrawGeometry(BatBody, null, Poly(cx - 5.4, y - 3, cx - 4.6, y - 9, cx - 1.2, y - 5));
        dc.DrawGeometry(BatBody, null, Poly(cx + 5.4, y - 3, cx + 4.6, y - 9, cx + 1.2, y - 5));
        dc.DrawEllipse(BatBody, null, new Point(cx, y + 0.6), 5.8, 5.6);
        Eye(dc, cx - 2.3, y - 0.2, 1.6, 2, mood, Brushes.White, Ink, small: true);
        Eye(dc, cx + 2.3, y - 0.2, 1.6, 2, mood, Brushes.White, Ink, small: true);
        dc.DrawGeometry(Paper, null, Poly(cx - 1.6, y + 3, cx - 0.5, y + 3, cx - 1.05, y + 4.5));
        dc.DrawGeometry(Paper, null, Poly(cx + 1.6, y + 3, cx + 0.5, y + 3, cx + 1.05, y + 4.5));
    }

    // ------------------------------------------------------------------ sky and elements

    private static readonly Brush MoonGold = Rgb(0xFF, 0xE0, 0x82);

    private void DrawMoon(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        double rock = Math.Sin(_t * (mood == CharacterMood.Music ? 6 : 1.3)) * (mood == CharacterMood.Music ? 8 + _beat * 10 : 5);
        dc.PushTransform(new RotateTransform(rock, cx, y + 6));
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx - 1, y), 9.4, 9.4), new EllipseGeometry(new Point(cx + 4.4, y - 2.2), 8, 8));
        dc.DrawGeometry(MoonGold, null, crescent);
        Eye(dc, cx - 5.6, y + 0.4, 1.5, 1.9, mood, Ink, Brushes.White, small: true);
        Blush(dc, cx - 5.2, y + 4, 1.3);
        dc.Pop();
        // two stars keep it company
        for (int i = 0; i < 2; i++)
        {
            double life = (_t * 0.6 + i * 0.5) % 1;
            dc.PushOpacity(0.25 + 0.75 * Math.Sin(life * Math.PI));
            dc.DrawEllipse(Brushes.White, null, new Point(cx + 8 + i * 3.4, y - 5 + i * 8), 1 - i * 0.2, 1 - i * 0.2);
            dc.Pop();
        }
    }

    private static readonly Brush SunCore = Rgb(0xFF, 0xC2, 0x3A);

    private void DrawSun(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        double spin = _t * (mood == CharacterMood.Music ? 50 + _beat * 140 : mood == CharacterMood.Sleep ? 4 : 16);
        double grow = mood == CharacterMood.Music ? _beat * 1.8 : Math.Sin(_t * 2.4) * 0.4;
        var ray = FrozenPen(SunCore, 1.8);
        dc.PushTransform(new RotateTransform(spin, cx, y + 0.5));
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            dc.DrawLine(ray, new Point(cx + Math.Cos(a) * 8.2, y + 0.5 + Math.Sin(a) * 8.2),
                             new Point(cx + Math.Cos(a) * (10.2 + grow), y + 0.5 + Math.Sin(a) * (10.2 + grow)));
        }
        dc.Pop();
        dc.DrawEllipse(SunCore, null, new Point(cx, y + 0.5), 6.6, 6.6);
        DarkEyes(dc, cx, y, 2.5, 1.5, 1.9, mood);
        Mouth(dc, cx, y + 3.2, mood, 1.2, Ink);
        Blush(dc, cx - 4.4, y + 2.4, 1.2);
        Blush(dc, cx + 4.4, y + 2.4, 1.2);
    }

    /// <summary>A teardrop: round at the bottom, a tip at the top that can lean.</summary>
    private static Geometry Teardrop(double cx, double y, double r, double tipX, double tipY)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(tipX, tipY), true, true);
            c.BezierTo(new Point(cx + r * 0.3, y - r * 0.6), new Point(cx + r * 1.08, y - r * 0.2), new Point(cx + r, y + r * 0.45), true, true);
            c.ArcTo(new Point(cx - r, y + r * 0.45), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.BezierTo(new Point(cx - r * 1.08, y - r * 0.2), new Point(cx - r * 0.3, y - r * 0.6), new Point(tipX, tipY), true, true);
        }
        return g;
    }

    private static readonly Brush FlameOuter = Rgb(0xFF, 0x74, 0x2B), FlameInner = Rgb(0xFF, 0xD3, 0x5A);

    private void DrawFlame(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // it flickers by itself and leaps with the music; asleep it burns low
        double low = mood == CharacterMood.Sleep ? 3 : 0;
        double lean = Math.Sin(_t * 6.3) * 1.5 + Math.Sin(_t * 11.1) * 0.7;
        double tall = Math.Sin(_t * 9.7) * 0.9 + _beat * 3.4 - low;
        dc.DrawGeometry(FlameOuter, null, Teardrop(cx, y + 1.6, 7.2, cx + lean, y - 9.4 - tall));
        dc.DrawGeometry(FlameInner, null, Teardrop(cx, y + 3.4, 4.9, cx + lean * 0.5, y - 4.4 - tall * 0.5));
        DarkEyes(dc, cx, y + 3, 2.1, 1.3, 1.7, mood);
    }

    private void DrawDrop(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // wobbles like water; about to fall when it dances
        double sx = 1 + Math.Sin(_t * 4) * 0.035 + _beat * 0.07, sy = 1 - Math.Sin(_t * 4) * 0.035 - _beat * 0.05;
        dc.PushTransform(new ScaleTransform(sx, sy, cx, y + 8));
        dc.DrawGeometry(Blue, null, Teardrop(cx, y + 1.6, 7.4, cx, y - 9.6));
        dc.PushOpacity(0.7);
        dc.DrawEllipse(Brushes.White, null, new Point(cx - 3.6, y - 0.6), 1.2, 2.1);
        dc.Pop();
        DarkEyes(dc, cx, y + 2.6, 2.6, 1.5, 1.9, mood);
        Blush(dc, cx - 4.6, y + 5, 1.2);
        Blush(dc, cx + 4.6, y + 5, 1.2);
        dc.Pop();
    }

    private void DrawHeart(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        // two beats and a rest, like a real one; in time with the music when it plays
        double phase = _t * (mood == CharacterMood.Sleep ? 0.8 : 1.3) % 1;
        double beat = mood == CharacterMood.Music ? _beat * 0.2
                    : Math.Max(0, Math.Sin(phase * Math.PI * 6)) * (phase < 0.34 ? 0.1 : 0);
        dc.PushTransform(new ScaleTransform(1 + beat, 1 + beat, cx, y + 1));
        dc.DrawGeometry(LoveBrush, null, HeartGeometry(cx, y + 1, 9.4));
        dc.PushOpacity(0.55);
        dc.DrawEllipse(Brushes.White, null, new Point(cx - 4.4, y - 3.4), 1.4, 1);
        dc.Pop();
        DarkEyes(dc, cx, y + 0.4, 2.7, 1.5, 1.9, mood);
        Mouth(dc, cx, y + 3.6, mood, 1.1, Ink);
        dc.Pop();
    }

    private static readonly Brush PumpkinLight = Rgb(0xFF, 0xA8, 0x45), PumpkinGlow = Rgb(0xFF, 0xE8, 0x7A), PumpkinStem = Rgb(0x5A, 0x9E, 0x4B);

    private void DrawPumpkin(DrawingContext dc, double cx, double y, CharacterMood mood)
    {
        dc.DrawRoundedRectangle(PumpkinStem, null, new Rect(cx - 1.3, y - 9.6, 2.6, 4), 1, 1);
        dc.DrawEllipse(Orange, null, new Point(cx - 5, y + 1.4), 5.4, 6.6);
        dc.DrawEllipse(Orange, null, new Point(cx + 5, y + 1.4), 5.4, 6.6);
        dc.DrawEllipse(PumpkinLight, null, new Point(cx, y + 1.4), 5.4, 7);
        // the candle inside flickers
        double glow = 0.8 + Math.Sin(_t * 9) * 0.1 + Math.Sin(_t * 14.3) * 0.08;
        dc.PushOpacity(mood == CharacterMood.Sleep ? 0.45 : glow);
        Eye(dc, cx - 3.6, y - 0.4, 2, 2.3, mood, Ink, PumpkinGlow, small: true);
        Eye(dc, cx + 3.6, y - 0.4, 2, 2.3, mood, Ink, PumpkinGlow, small: true);
        dc.Pop();
        // a zigzag grin
        var grin = new StreamGeometry();
        using (var c = grin.Open())
        {
            c.BeginFigure(new Point(cx - 4.6, y + 3.6), false, false);
            for (int i = 1; i <= 6; i++) c.LineTo(new Point(cx - 4.6 + i * 1.53, y + 3.6 + (i % 2 == 1 ? 1.5 : 0)), true, true);
        }
        dc.DrawGeometry(null, InkLine, grin);
    }
}
