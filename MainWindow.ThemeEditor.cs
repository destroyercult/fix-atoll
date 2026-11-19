using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Your own theme: island colour, accent, scene; saved next to the built-in ones. A theme travels as one
/// line of text ("atoll-theme:…"): copy it to share, paste one to add it. Only a name and colours are read.
/// </summary>
public partial class MainWindow
{
    private const string ThemeCodePrefix = "atoll-theme:1:";
    private static readonly string[] AccentChoices = ["#30D158", "#0A84FF", "#BF5AF2", "#FF375F", "#FF9F0A", "#FFD60A", "#64D2FF", "#B9A4FF"];

    private TextBox? _themeName, _themeIsland, _themeAccent, _themeCode;
    private TextBlock? _themeStatus;
    private bool _themeEditorLoading;

    private void BuildThemeEditor()
    {
        _themeEditorLoading = true;
        if (_themeName == null)
        {
            var host = ThemeEditorHost;
            host.Children.Add(new TextBlock { Style = (Style)FindResource("Caption"), Text = L.T("СВОЯ ТЕМА"), Margin = new Thickness(0, 6, 0, 8) });

            TextBox Box(double width, string tip) => new() { Style = (Style)FindResource("SoftBox"), Width = width, Height = 30, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
            TextBlock Label(string text) => new() { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

            _themeIsland = Box(92, L.T("Цвет острова, например #101820"));
            _themeAccent = Box(92, L.T("Цвет акцента: кнопки, полоски, значки"));
            var colors = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var a = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 6) };
            a.Children.Add(Label(L.T("Остров"))); a.Children.Add(_themeIsland);
            var b = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            b.Children.Add(Label(L.T("Акцент"))); b.Children.Add(_themeAccent);
            colors.Children.Add(a); colors.Children.Add(b);
            host.Children.Add(colors);

            // quick accents
            var dots = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var hex in AccentChoices)
            {
                var d = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 4), Cursor = System.Windows.Input.Cursors.Hand,
                                     Background = new SolidColorBrush(Theme.Parse(hex) ?? Colors.White), ToolTip = hex };
                d.MouseLeftButtonUp += (_, _) => { _themeAccent!.Text = hex; };
                dots.Children.Add(d);
            }
            host.Children.Add(dots);

            // the scene inside the island
            var scenes = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (id, name) in new[] { ("", L.T("Без сцены")), ("liquid", L.T("Жидкое стекло")), ("spring", L.T("Весна")), ("summer", L.T("Лето")), ("autumn", L.T("Осень")), ("winter", L.T("Зима")) })
            {
                var chip = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "ThemeScene", Content = name, Tag = id, Margin = new Thickness(0, 0, 4, 4) };
                chip.Checked += (_, _) => { if (!_themeEditorLoading) PreviewOwnTheme(); };
                scenes.Children.Add(chip);
            }
            host.Children.Add(scenes);
            _themeSceneChips = scenes;

            _themeName = Box(150, L.T("Название темы"));
            var save = new Button { Style = (Style)FindResource("AccentPill"), Content = L.T("Сохранить тему"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
            save.Click += (_, _) => SaveOwnTheme();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Label(L.T("Название"))); row.Children.Add(_themeName); row.Children.Add(save);
            host.Children.Add(row);

            _themeCode = Box(200, L.T("Код темы: atoll-theme:…"));
            var add = new Button { Style = (Style)FindResource("GhostPill"), Content = L.T("Добавить по коду"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
            add.Click += (_, _) => ImportTheme(_themeCode!.Text);
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row2.Children.Add(Label(L.T("Код темы"))); row2.Children.Add(_themeCode); row2.Children.Add(add);
            host.Children.Add(row2);

            _themeStatus = new TextBlock { Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap,
                Text = L.T("Свои темы появляются в списке выше. Правый клик по своей теме — поделиться кодом или удалить.") };
            host.Children.Add(_themeStatus);

            _themeIsland.TextChanged += (_, _) => { if (!_themeEditorLoading) PreviewOwnTheme(); };
            _themeAccent.TextChanged += (_, _) => { if (!_themeEditorLoading) PreviewOwnTheme(); };
        }
        _themeIsland!.Text = _settings.IslandColor;
        _themeAccent!.Text = _settings.AccentColor.Length > 0 ? _settings.AccentColor : "#30D158";
        foreach (RadioButton chip in _themeSceneChips!.Children) chip.IsChecked = (string)chip.Tag == (_settings.Scene ?? "");
        _themeEditorLoading = false;
    }

    private WrapPanel? _themeSceneChips;

    private string EditorScene => _themeSceneChips!.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag as string ?? "";

    /// <summary>What is typed shows on the island right away (once both colours are valid).</summary>
    private void PreviewOwnTheme()
    {
        var island = NormalizeHex(_themeIsland!.Text);
        var accent = NormalizeHex(_themeAccent!.Text);
        if (!IsHex(island) || !IsHex(accent)) return;
        _settings.IslandColor = island;
        _settings.AccentColor = accent;
        _settings.Scene = EditorScene;
        if (_settings.Scene == "liquid") _settings.Glass = true;
        _settings.ThemeId = "";
        _settings.Save();
        ApplyScene();
        ApplyTheme();
        ApplyThemeLocal();
        ApplyGlass();
        ColorBox.Text = island;
        GlassSwitch.IsChecked = _settings.Glass;
    }

    private void SaveOwnTheme()
    {
        var island = NormalizeHex(_themeIsland!.Text);
        var accent = NormalizeHex(_themeAccent!.Text);
        var name = _themeName!.Text.Trim();
        if (!IsHex(island) || !IsHex(accent)) { _themeStatus!.Text = L.T("Цвет пишется так: #101820 — решётка и шесть знаков."); return; }
        if (name.Length == 0) { _themeStatus!.Text = L.T("Дайте теме название."); _themeName.Focus(); return; }
        if (name.Length > 30) name = name[..30];
        var t = new CustomTheme { Id = Guid.NewGuid().ToString("N")[..8], Name = name, Dark = _settings.DarkTheme, Island = island, Accent = accent, Aura = _settings.Aura, Scene = EditorScene };
        _settings.CustomThemes.Add(t);
        _settings.ThemeId = "own:" + t.Id;
        _settings.Save();
        _themeName.Text = "";
        _themeStatus!.Text = L.F("Тема «{0}» сохранена.", name);
        BuildThemeGallery();
        Remeasure();
    }

    private static ThemePreset ToPreset(CustomTheme t) => new("own:" + t.Id, t.Name, t.Dark, t.Island, t.Accent, t.Aura, t.Scene);

    /// <summary>One line of text that carries a theme. The name is escaped; everything else is checked when read back.</summary>
    private static string ThemeCode(CustomTheme t) =>
        ThemeCodePrefix + string.Join("|", Uri.EscapeDataString(t.Name), t.Dark ? "1" : "0", t.Island, t.Accent, t.Aura, t.Scene);

    private static CustomTheme? ParseThemeCode(string code)
    {
        code = code.Trim();
        if (!code.StartsWith(ThemeCodePrefix, StringComparison.OrdinalIgnoreCase) || code.Length > 400) return null;
        var p = code[ThemeCodePrefix.Length..].Split('|');
        if (p.Length != 6) return null;
        string name;
        try { name = Uri.UnescapeDataString(p[0]).Trim(); } catch { return null; }
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        if (name.Length is 0 or > 30 || !IsHex(p[2]) || !IsHex(p[3]) || p[1] is not ("0" or "1")) return null;
        var aura = Auras.Any(a => a.Id == p[4]) ? p[4] : "none";
        var scene = Array.IndexOf(Views.SeasonView.Kinds, p[5]) >= 0 ? p[5] : "";
        return new CustomTheme { Id = Guid.NewGuid().ToString("N")[..8], Name = name, Dark = p[1] == "1", Island = p[2], Accent = p[3], Aura = aura, Scene = scene };
    }

    private void ImportTheme(string code)
    {
        var t = ParseThemeCode(code);
        if (t == null) { _themeStatus!.Text = L.T("Это не код темы. Он начинается с «atoll-theme:»."); return; }
        if (_settings.CustomThemes.Count >= 40) { _themeStatus!.Text = L.T("Слишком много своих тем — удалите лишние."); return; }
        _settings.CustomThemes.Add(t);
        _settings.Save();
        _themeCode!.Text = "";
        _themeStatus!.Text = L.F("Тема «{0}» добавлена.", t.Name);
        ApplyThemePreset(ToPreset(t));
        Remeasure();
    }

    /// <summary>The user's themes in the gallery: right-click to share or delete.</summary>
    private void AddOwnThemeCards()
    {
        foreach (var t in _settings.CustomThemes.ToList())
        {
            var own = t;
            var card = ThemeCard(ToPreset(own), own.Name);
            var menu = new ContextMenu();
            var share = new MenuItem { Header = L.T("Скопировать код темы") };
            share.Click += (_, _) =>
            {
                try { Clipboard.SetText(ThemeCode(own)); } catch { return; }
                ShowToast("", L.T("Код темы скопирован"), L.T("Отправьте его другу или в Telegram-канал"));
            };
            var del = new MenuItem { Header = L.T("Удалить тему") };
            del.Click += (_, _) =>
            {
                _settings.CustomThemes.RemoveAll(x => x.Id == own.Id);
                _settings.Save();
                BuildThemeGallery();
                Remeasure();
            };
            menu.Items.Add(share); menu.Items.Add(del);
            card.ContextMenu = menu;
            ThemeGallery.Children.Add(card);
        }
    }
}
