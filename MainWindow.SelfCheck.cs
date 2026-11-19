using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Settings → Check: one page that says, in plain words, what works, what doesn't and why — with a button
/// that fixes it where a fix exists. Also copies the same as text, to send when asking for help.
/// </summary>
public partial class MainWindow
{
    private enum CheckState { Ok, Warn, Bad, Off }

    private sealed record CheckRow(string Name, CheckState State, string Text, string? Fix = null, Action? Run = null);

    private bool _checkHooked;
    private List<CheckRow> _checkRows = [];

    private void InitSelfCheck()
    {
        if (_checkHooked) return;
        _checkHooked = true;
        CheckHost.IsVisibleChanged += (_, _) => { if (CheckHost.IsVisible) _ = RunSelfCheckAsync(); };
    }

    private async Task RunSelfCheckAsync()
    {
        CheckHost.Children.Clear();
        CheckHost.Children.Add(new TextBlock { Style = (Style)FindResource("Muted"), Text = L.T("Проверяю…"), Margin = new Thickness(2, 2, 0, 0) });
        var rows = await CollectChecksAsync();
        _checkRows = rows;
        CheckHost.Children.Clear();

        int bad = rows.Count(r => r.State == CheckState.Bad), warn = rows.Count(r => r.State == CheckState.Warn);
        var head = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10), TextWrapping = TextWrapping.Wrap,
            Text = bad + warn == 0 ? L.T("Всё работает") : L.F("Требуют внимания: {0}", bad + warn) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        CheckHost.Children.Add(head);

        foreach (var r in rows)
        {
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(r.State switch
                {
                    CheckState.Ok => Color.FromRgb(0x30, 0xD1, 0x58),
                    CheckState.Warn => Color.FromRgb(0xFF, 0x9F, 0x0A),
                    CheckState.Bad => Color.FromRgb(0xFF, 0x45, 0x3A),
                    _ => Color.FromRgb(0x6E, 0x6E, 0x73),
                }) };
            var name = new TextBlock { Text = r.Name, FontSize = 13, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var text = new TextBlock { Text = r.Text, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            var col = new StackPanel();
            col.Children.Add(name); col.Children.Add(text);
            var row = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            if (r.Fix != null && r.Run != null)
            {
                var fix = new Button { Style = (Style)FindResource("GhostPill"), Content = r.Fix, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var run = r.Run;
                fix.Click += async (_, _) =>
                {
                    try { run(); } catch (Exception ex) { Log.Write("self-check fix: " + ex.Message); }
                    await Task.Delay(900);
                    await RunSelfCheckAsync();
                };
                DockPanel.SetDock(fix, Dock.Right);
                row.Children.Add(fix);
            }
            row.Children.Add(col);
            CheckHost.Children.Add(new Border { Style = (Style)FindResource("Tile"), Margin = new Thickness(0, 0, 0, 8), Child = row });
        }

        var again = new Button { Style = (Style)FindResource("GhostPill"), Content = L.T("Проверить снова"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        again.Click += async (_, _) => await RunSelfCheckAsync();
        var copy = new Button { Style = (Style)FindResource("GhostPill"), Content = L.T("Скопировать отчёт"), Padding = new Thickness(12, 6, 12, 6), ToolTip = L.T("Текстом: его можно отправить в Telegram-канал, если нужна помощь") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(SelfCheckReport()); } catch { return; }
            ShowToast("", L.T("Отчёт скопирован"), L.T("Вставьте его в сообщение: Ctrl+V"));
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        bar.Children.Add(again); bar.Children.Add(copy);
        CheckHost.Children.Add(bar);
        Remeasure();
    }

    private async Task<List<CheckRow>> CollectChecksAsync()
    {
        var rows = new List<CheckRow>();
        void OpenSettings(string page) => Process.Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true });

        // messages
        if (!_settings.MirrorNotifications)
            rows.Add(new(L.T("Сообщения мессенджеров"), CheckState.Off, L.T("Выключено в настройках."), L.T("Включить"),
                () => { _settings.MirrorNotifications = true; _settings.Save(); RefreshSettingsPage(); }));
        else if (NotificationMirror.CenterDisabled())
            rows.Add(new(L.T("Сообщения мессенджеров"), CheckState.Warn,
                L.T("Центр уведомлений Windows отключён (так делают программы-«твикеры»). Остров показывает «новое сообщение» по счётчику непрочитанных в окне мессенджера, но без текста. Чтобы видеть текст, включите центр уведомлений обратно в той же программе.")));
        else if (!_notifications.Available)
            rows.Add(new(L.T("Сообщения мессенджеров"), CheckState.Warn,
                L.T("Windows не дал доступ к уведомлениям: остров показывает только «новое сообщение», без текста."), L.T("Открыть параметры"),
                () => OpenSettings("privacy-notifications")));
        else
            rows.Add(new(L.T("Сообщения мессенджеров"), CheckState.Ok,
                L.T("Доступ есть. В Telegram Desktop должны быть включены «Системные уведомления», иначе Windows его сообщений не видит.")));

        // music
        rows.Add(_media.Ready
            ? new(L.T("Музыка"), CheckState.Ok, _lastMedia != null ? L.F("Вижу плеер: {0}", Shorten(_lastMedia.Title ?? "", 40)) : L.T("Работает. Включите музыку в любом плеере или браузере — она появится на острове."))
            : new(L.T("Музыка"), CheckState.Bad, L.T("Windows не отдаёт сведения о музыке. Обычно помогает перезапуск Atoll или компьютера.")));

        // weather
        if (string.IsNullOrWhiteSpace(_settings.City))
            rows.Add(new(L.T("Погода"), CheckState.Off, L.T("Город не указан."), L.T("Указать город"), () => OpenTab(TabWeather)));
        else if (_weatherFailures > 0)
            rows.Add(new(L.T("Погода"), CheckState.Bad, L.T("Нет связи с сервисами погоды. Проверьте интернет; Atoll сам повторяет попытки."), L.T("Обновить"), () => _ = RefreshWeatherAsync()));
        else if (!_weatherLoaded)
            rows.Add(new(L.T("Погода"), CheckState.Warn, L.T("Ещё не загружена."), L.T("Обновить"), () => _ = RefreshWeatherAsync()));
        else
            rows.Add(new(L.T("Погода"), CheckState.Ok, _weather.Source.Length > 0 ? L.F("Обновляется. Источник: {0}", _weather.Source) : L.T("Обновляется.")));

        // FPS in games
        if (!_settings.GameStats)
            rows.Add(new(L.T("FPS в играх"), CheckState.Off, L.T("Выключено: в играх остров прячется целиком. Включается в разделе «Поведение».")));
        else
        {
            bool ok = _fps.Running || FpsMeter.HelperAlive() || await Task.Run(ProbeFps);
            rows.Add(ok
                ? new(L.T("FPS в играх"), CheckState.Ok, L.T("Счётчик работает: в играх он появится в пилюле."))
                : new(L.T("FPS в играх"), CheckState.Warn, L.T("Нужно один раз разрешить доступ — Windows спросит подтверждение."), L.T("Разрешить"), () => FpsGrant_Click(this, new RoutedEventArgs())));
        }

        // glass
        if (_settings.Glass && !Lite)
            rows.Add(FrostCanGrab
                ? new(L.T("Матовое стекло"), CheckState.Ok, L.T("Работает: остров показывает размытый фон за собой."))
                : new(L.T("Матовое стекло"), CheckState.Warn, L.T("Сейчас стекло сплошное: Windows не дал снять фон за островом.")));
        else if (Lite)
            rows.Add(new(L.T("Матовое стекло"), CheckState.Off, L.T("Отключено облегчённой графикой (слабый ПК или экономия заряда).")));

        // start with Windows
        if (!AppPackage.IsPackaged)
            rows.Add(Autostart.IsEnabled
                ? new(L.T("Запуск с Windows"), CheckState.Ok, L.T("Остров запускается сам при входе в систему."))
                : new(L.T("Запуск с Windows"), CheckState.Warn, L.T("После перезагрузки остров придётся открывать вручную."), L.T("Включить"), () => Autostart.Set(true)));

        // updates
        if (!AppPackage.IsPackaged)
            rows.Add(_update == null
                ? new(L.T("Обновления"), _settings.AutoUpdateCheck ? CheckState.Ok : CheckState.Warn,
                      _settings.AutoUpdateCheck ? L.F("Установлена последняя версия · {0}", Updater.Current.ToString(3)) : L.T("Автопроверка выключена: новые версии сами не придут."),
                      _settings.AutoUpdateCheck ? null : L.T("Включить"),
                      _settings.AutoUpdateCheck ? null : () => { _settings.AutoUpdateCheck = true; _settings.Save(); AutoUpdateSwitch.IsChecked = true; })
                : new(L.T("Обновления"), CheckState.Warn, L.F("Доступна версия {0} (у вас {1})", _update.Version.ToString(3), Updater.Current.ToString(3)), L.T("Обновить"), StartUpdate));

        // calendar
        if (_settings.CalendarIcs.Trim().Length == 0)
            rows.Add(new(L.T("Календарь"), CheckState.Off, L.T("Не подключён. Напоминания работают и без него; ссылку календаря можно добавить на вкладке «Календарь».")));
        else
            rows.Add(_calendar.Error == null
                ? new(L.T("Календарь"), CheckState.Ok, L.T("Подключён: за 10 минут до события остров напомнит, а по клику откроет ссылку на звонок."))
                : new(L.T("Календарь"), CheckState.Bad, L.T("Не получается загрузить календарь: проверьте ссылку и интернет."), L.T("Открыть"), () => OpenTab(TabCalendar)));

        return rows;
    }

    /// <summary>The same as text (no personal data: no city, no titles, no links).</summary>
    private string SelfCheckReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Atoll {Updater.Current.ToString(3)} · Windows {Environment.OSVersion.Version.Build} · {(AppPackage.IsPackaged ? "Store" : Updater.CanSelfUpdate ? "installed" : "portable")} · {Loc.Lang}");
        foreach (var r in _checkRows)
        {
            var mark = r.State switch { CheckState.Ok => "OK", CheckState.Warn => "!", CheckState.Bad => "X", _ => "-" };
            var text = r.Name == L.T("Музыка") && r.State == CheckState.Ok ? "" : r.Text;   // the song's name stays out of it
            sb.AppendLine($"[{mark}] {r.Name}{(text.Length > 0 ? ": " + text : "")}");
        }
        sb.AppendLine($"Glass {(_settings.Glass ? "on" : "off")}, lite {(Lite ? "on" : "off")}, scale {_settings.IslandScale}%, theme {(_settings.ThemeId.Length > 0 ? _settings.ThemeId : "custom")}");
        return sb.ToString();
    }
}
