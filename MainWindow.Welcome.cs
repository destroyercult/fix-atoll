using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The first start: three short steps (a look, what to show, how to use it). And after an update:
/// a notice that opens "what's new" for that version, with the Telegram channel one click away.
/// </summary>
public partial class MainWindow
{
    private Window? _card;

    private void InitWelcome()
    {
        if (IsToolRun || _demoMedia != null) return;
        var ver = Updater.Current.ToString(3);
        if (!_settings.Onboarded)
        {
            _settings.Onboarded = true;
            _settings.SeenVersion = ver;
            _settings.Save();
            Dispatcher.InvokeAsync(async () => { await Task.Delay(1500); ShowWelcome(); });
            return;
        }
        if (_settings.SeenVersion == ver) return;
        _settings.SeenVersion = ver;
        _settings.Save();
        if (WhatsNew(ver).Count == 0) return;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(4000);   // let the island settle after the update
            ShowToast("", L.F("Atoll обновился до {0}", ver), L.T("Нажмите — что нового"), () => ShowWhatsNew(ver),
                (Brush)FindResource("AccentBlue"), ms: 9000);
        });
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) => ShowWhatsNew(Updater.Current.ToString(3));

    // ------------------------------------------------------------------ the card under the island

    /// <summary>A small rounded window under the island: a title, a body, a row of buttons.</summary>
    private (Window Win, StackPanel Body, StackPanel Buttons, TextBlock Title) OpenCard(double width)
    {
        _card?.Close();
        var title = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 28, 12) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var body = new StackPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 28, Height = 28, FontSize = 11,
                                 HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -6, 0) };
        var stack = new StackPanel();
        stack.Children.Add(title); stack.Children.Add(body); stack.Children.Add(buttons);
        var grid = new Grid();
        grid.Children.Add(stack); grid.Children.Add(close);
        var panel = new Border { CornerRadius = new CornerRadius(26), Padding = new Thickness(22, 20, 22, 18), Margin = new Thickness(24), Child = grid,
                                 BorderThickness = new Thickness(1), Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.45, Direction = 270 } };
        panel.Background = new SolidColorBrush(_settings.DarkTheme ? Color.FromRgb(0x14, 0x14, 0x17) : Color.FromRgb(0xF4, 0xF4, 0xF7));
        panel.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = width + 48, Content = panel, Title = "Atoll",
            FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Fg");
        // under the island, on its monitor
        win.Left = Left + Width / 2 - win.Width / 2;
        win.Top = Top + IslandTopMargin + 54;
        panel.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };
        close.Click += (_, _) => win.Close();
        win.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.Closed += (_, _) => { if (ReferenceEquals(_card, win)) _card = null; };
        _card = win;
        return (win, body, buttons, title);
    }

    private Button CardButton(string text, string style, Action click)
    {
        var b = new Button { Style = (Style)FindResource(style), Content = text, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private TextBlock CardText(string text, bool muted = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = muted ? 12.5 : 13.5, Margin = new Thickness(0, 0, 0, 8), LineHeight = 19 };
        t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Fg2" : "Fg");
        return t;
    }

    // ------------------------------------------------------------------ the first start

    private static readonly string[] WelcomeThemes = ["midnight", "liquid", "graphite", "ocean", "lavender", "sunset", "winter", "snow"];

    private void ShowWelcome(int step = 0)
    {
        var (win, body, buttons, title) = OpenCard(464);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

        void Row(string glyph, string head, string text)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 17, Width = 34, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var h = new TextBlock { Text = head, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
            var col = new StackPanel(); col.Children.Add(h); col.Children.Add(t);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon); row.Children.Add(col);
            body.Children.Add(row);
        }

        CheckBox Toggle(string text, bool on, Action<bool> set)
        {
            var c = new CheckBox { Style = (Style)FindResource("Switch"), Content = text, IsChecked = on };
            c.Click += (_, _) => { set(c.IsChecked == true); _settings.Save(); };
            return c;
        }

        void Render()
        {
            body.Children.Clear();
            buttons.Children.Clear();
            dots.Children.Clear();
            for (int i = 0; i < 3; i++)
            {
                var d = new System.Windows.Shapes.Ellipse { Width = i == step ? 7 : 5, Height = i == step ? 7 : 5, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
                d.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, i == step ? "Fg" : "Fg3");
                dots.Children.Add(d);
            }
            if (step == 0)
            {
                title.Text = L.T("Добро пожаловать в Atoll");
                body.Children.Add(CardText(L.T("Остров уже наверху экрана. Выберите, как он выглядит — поменять можно в любой момент в настройках."), muted: true));
                var cards = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var id in WelcomeThemes)
                {
                    var t = Array.Find(BuiltInThemes, x => x.Id == id);
                    if (t == null) continue;
                    var card = ThemeCard(t, L.T(t.Name));
                    card.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(Render, System.Windows.Threading.DispatcherPriority.Background);   // mark the chosen one
                    cards.Children.Add(card);
                }
                body.Children.Add(cards);
            }
            else if (step == 1)
            {
                title.Text = L.T("Что показывать на острове");
                body.Children.Add(Toggle(L.T("Сообщения Telegram, Discord, Viber…"), _settings.MirrorNotifications, v => _settings.MirrorNotifications = v));
                body.Children.Add(Toggle(L.T("Громкость и яркость рядом с островом"), _settings.ShowLevels, v => _settings.ShowLevels = v));
                body.Children.Add(Toggle(L.T("Слова песни прямо в острове (караоке)"), _settings.Karaoke, v => _settings.Karaoke = v));
                body.Children.Add(Toggle(L.T("Прятать в играх и полноэкранном режиме"), _settings.HideInFullscreen, v => { _settings.HideInFullscreen = v; if (v) _settings.GameStats = false; }));
                if (!AppPackage.IsPackaged)
                    body.Children.Add(Toggle(L.T("Запускать вместе с Windows"), Autostart.IsEnabled, v => Autostart.Set(v)));
                body.Children.Add(CardText(L.T("Музыка, таймер, погода, буфер обмена и заметки работают сразу — их включать не нужно."), muted: true));
            }
            else
            {
                title.Text = L.T("Как им пользоваться");
                Row("", L.T("Наведите курсор"), L.T("Остров раскроется: музыка, погода, таймер, буфер, заметки, настройки."));
                Row("", L.T("Смахните вверх — спрячется"), L.T("Вернуть: Ctrl + Alt + I или полоска у края экрана."));
                Row("", L.T("Перетащите файл на остров"), L.T("Отправить в чат, сжать фото, упаковать в ZIP или положить на полку."));
                Row("", L.T("Alt + Пробел — строка команд"), L.T("Запуск программ, калькулятор, «таймер 10», «напомни…»."));
                Row("", L.T("Колесо мыши над островом"), L.T("Переключает то, что он показывает: таймер, музыку, звонок."));
            }

            buttons.Children.Add(dots);
            if (step > 0) buttons.Children.Add(CardButton(L.T("Назад"), "GhostPill", () => { step--; Render(); }));
            if (step == 2) buttons.Children.Add(CardButton(L.T("Telegram-канал"), "GhostPill", () => SafeShell.OpenUrl(TelegramUrl)));
            buttons.Children.Add(step < 2
                ? CardButton(L.T("Далее"), "AccentPill", () => { step++; Render(); })
                : CardButton(L.T("Готово"), "AccentPill", () => { RefreshSettingsPage(); win.Close(); }));
        }

        Render();
        win.Show();
    }

    // ------------------------------------------------------------------ what's new

    /// <summary>The lines of this version from the change log that ships inside the program.</summary>
    private static List<string> WhatsNew(string version)
    {
        var result = new List<string>();
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DynamicIsland.CHANGELOG.md");
            if (s == null) return result;
            using var r = new StreamReader(s);
            var lines = r.ReadToEnd().Replace("\r", "").Split('\n');
            int start = Array.FindIndex(lines, l => l.StartsWith("## " + version + " ") || l.Trim() == "## " + version);
            if (start < 0) return result;
            bool russian = Loc.Lang is "ru" or "uk";
            bool inRussian = false, hasRussian = false;
            for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## "); i++)
                if (lines[i].StartsWith("### По-русски")) hasRussian = true;
            for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## "); i++)
            {
                var l = lines[i];
                if (l.StartsWith("### По-русски")) { inRussian = true; continue; }
                if (!l.StartsWith("- ")) continue;
                if (russian && hasRussian ? !inRussian : inRussian) continue;
                var text = l[2..].Replace("**", "").Replace("`", "").Trim();
                // the card is a summary: the name and the first sentence; the full text is on the release page
                int colon = text.IndexOf(": ", StringComparison.Ordinal);
                int stop = text.IndexOf(". ", Math.Max(0, colon), StringComparison.Ordinal);
                if (stop > 0 && text.Length > 150) text = text[..(stop + 1)];
                result.Add(text);
            }
        }
        catch { }
        return result;
    }

    private void ShowWhatsNew(string version)
    {
        var lines = WhatsNew(version);
        var (win, body, buttons, title) = OpenCard(460);
        title.Text = L.F("Atoll {0} — что нового", version);
        if (lines.Count == 0) body.Children.Add(CardText(L.T("Исправления и мелкие улучшения."), muted: true));
        var list = new StackPanel();
        foreach (var line in lines)
        {
            var dot = new TextBlock { Text = "•", Width = 16, FontSize = 14 };
            dot.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            // "Name: what it does" — the name stands out
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            if (colon is > 0 and < 48)
            {
                t.Inlines.Add(new System.Windows.Documents.Run(line[..colon]) { FontWeight = FontWeights.SemiBold });
                var rest = new System.Windows.Documents.Run(line[colon..]);
                rest.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Fg2");
                t.Inlines.Add(rest);
            }
            else t.Text = line;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot); row.Children.Add(t);
            list.Children.Add(row);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        buttons.Children.Add(CardButton(L.T("Telegram-канал"), "GhostPill", () => SafeShell.OpenUrl(TelegramUrl)));
        if (!AppPackage.IsPackaged) buttons.Children.Add(CardButton(L.T("Все версии"), "GhostPill", () => SafeShell.OpenUrl("https://github.com/Fuwni/dynamic-island-windows/releases")));
        buttons.Children.Add(CardButton(L.T("Понятно"), "AccentPill", win.Close));
        win.Show();
    }
}
