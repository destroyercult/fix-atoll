using System.Windows;
using System.Text.RegularExpressions;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class MainWindow
{
    private GeminiLiveVoice? _voiceAssistant;
    private bool _voiceStarting;
    private string _lastVoiceLaunchCommand = "";
    private DateTime _lastVoiceLaunchAt;

    private async void VoiceAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceStarting) return;
        if (_voiceAssistant != null)
        {
            await StopVoiceAssistantAsync();
            return;
        }

        _voiceStarting = true;
        VoiceAssistantButton.IsEnabled = false;
        SetVoiceStatus("Подключаюсь…");
        try
        {
            var apiKey = GeminiLiveVoice.ResolveApiKey(_settings.GeminiVoiceApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetVoiceStatus("Укажите ключ в настройках «ИИ-агенты», GEMINI_API_KEY или KIK config.json");
                return;
            }

            var voice = _settings.GeminiVoice;
            var assistant = new GeminiLiveVoice(apiKey, voice);
            assistant.SetOutputVolume(_settings.GeminiVoiceVolume);
            _voiceAssistant = assistant;
            assistant.StatusChanged += status => Dispatcher.BeginInvoke(() =>
            {
                SetVoiceStatus(status);
            });
            assistant.UserTranscribed += text => Dispatcher.BeginInvoke(() => HandleVoiceTranscript(text));
            assistant.Failed += error => Dispatcher.BeginInvoke(async () =>
            {
                await StopVoiceAssistantAsync();
                SetVoiceStatus($"Ошибка Gemini Live: {error.Message}");
            });

            await assistant.StartAsync();
            VoiceAssistantButton.Content = "Стоп";
            VoiceAssistantButton.ToolTip = "Остановить голосовой диалог";
        }
        catch (Exception ex)
        {
            if (_voiceAssistant != null)
            {
                await _voiceAssistant.StopAsync();
                _voiceAssistant = null;
            }
            SetVoiceStatus($"Ошибка: {ex.Message}");
        }
        finally
        {
            _voiceStarting = false;
            VoiceAssistantButton.IsEnabled = true;
        }
    }

    private async Task StopVoiceAssistantAsync()
    {
        var assistant = _voiceAssistant;
        _voiceAssistant = null;
        if (assistant != null) await assistant.StopAsync();

        VoiceAssistantButton.Content = "Голос";
        VoiceAssistantButton.ToolTip = "Голосовой диалог Gemini Live";
        VoiceAssistantButton.IsEnabled = true;
        SetVoiceStatus("");
    }

    private void SetVoiceStatus(string status)
    {
        VoiceStatusText.Text = status;
        VoiceStatusText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HandleVoiceTranscript(string transcript)
    {
        var target = MatchVoiceLaunchCommand(transcript);
        if (target == null) return;

        var now = DateTime.UtcNow;
        if (target == _lastVoiceLaunchCommand && now - _lastVoiceLaunchAt < TimeSpan.FromSeconds(5)) return;
        _lastVoiceLaunchCommand = target;
        _lastVoiceLaunchAt = now;
        _ = LaunchVoiceTargetAsync(target);
    }

    private static string? MatchVoiceLaunchCommand(string transcript)
    {
        var match = Regex.Match(transcript.Trim(),
            @"^(?:пожалуйста\s+|please\s+)?(?:открой|откройте|открыть|запусти|запустите|запустить|включи|включите|включить|open|launch|start)\s+(?<target>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var target = match.Groups["target"].Value.Trim();
        target = Regex.Replace(target, @"\b(мне|пожалуйста|сейчас|приложение|программу|сайт|вебсайт|веб|web|website|страницу)\b", " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        target = Regex.Replace(target, @"\s+(точка|dot)\s+", ".", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        target = Regex.Replace(target, @"\s+(слэш|slash)\s+", "/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        target = Regex.Replace(target, @"\s+", " ").Trim();

        var directAddress = target.TrimEnd('.', ',', '!', '?', ';');
        if (directAddress.Contains('.') || directAddress.Contains('/') || directAddress.Contains(':'))
        {
            var directUrl = ToSafeWebUrl(directAddress);
            if (directUrl != null) return "url:" + directUrl;
        }

        var words = Regex.Replace(target.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ");
        words = Regex.Replace(words, @"\s+", " ").Trim();
        var aliases = new Dictionary<string, string[]>
        {
            ["youtube"] = ["ютуб", "ютюб", "youtube"],
            ["google"] = ["гугл", "google", "браузер", "хром", "chrome"],
            ["steam"] = ["стим", "steam"],
            ["roblox"] = ["роблокс", "roblox"],
            ["spotify"] = ["спотифай", "стопифай", "spotify"],
            ["telegram"] = ["телеграм", "телеграмм", "telegram", "тг"],
            ["discord"] = ["дискорд", "discord", "дс"],
        };
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var appTarget = aliases.FirstOrDefault(x => x.Value.Any(tokens.Contains)).Key;
        if (!string.IsNullOrEmpty(appTarget)) return appTarget;

        var address = target.Trim().TrimEnd('.', ',', '!', '?', ';');
        if (address.Contains(' ')) return null;
        var websiteUrl = ToSafeWebUrl(address);
        return websiteUrl == null ? null : "url:" + websiteUrl;
    }

    private static string? ToSafeWebUrl(string address)
    {
        var url = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? address
            : "https://" + address;
        if (!SafeShell.IsWebUrl(url, out var uri) || uri == null || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0)
            return null;
        return uri.AbsoluteUri;
    }

    private async Task LaunchVoiceTargetAsync(string target)
    {
        if (target.StartsWith("url:", StringComparison.Ordinal))
        {
            var websiteUrl = target[4..];
            SetVoiceStatus(SafeShell.OpenUrl(websiteUrl) ? "Открываю сайт…" : "Не удалось открыть сайт.");
            return;
        }

        var (name, appName, url) = target switch
        {
            "youtube" => ("YouTube", (string?)null, "https://www.youtube.com/"),
            "google" => ("Google", (string?)null, "https://www.google.com/"),
            "steam" => ("Steam", "Steam", (string?)null),
            "roblox" => ("Roblox", "Roblox", (string?)null),
            "spotify" => ("Spotify", "Spotify", (string?)null),
            "telegram" => ("Telegram", "Telegram", (string?)null),
            "discord" => ("Discord", "Discord", (string?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        if (url != null)
        {
            SetVoiceStatus(SafeShell.OpenUrl(url) ? $"Открываю {name}…" : $"Не удалось открыть {name}.");
            return;
        }

        try
        {
            await _apps.EnsureLoadedAsync();
            var app = _apps.Search(appName!, 10)
                .FirstOrDefault(candidate => target != "roblox" ||
                    !candidate.Name.Contains("studio", StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                if (target == "steam" && TryOpenSteamProtocol()) return;
                if (OpenVoiceWebFallback(target, name))
                {
                    return;
                }
                SetVoiceStatus($"{name} не найдено в меню «Пуск».");
                return;
            }

            var startInfo = AppIndex.IsStoreApp(app.Path)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + app.Path + "\"") { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo(app.Path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(startInfo);
            SetVoiceStatus($"Открываю {name}…");
        }
        catch (Exception ex)
        {
            if (target == "steam" && TryOpenSteamProtocol()) return;
            if (OpenVoiceWebFallback(target, name))
            {
                return;
            }
            SetVoiceStatus($"Не удалось открыть {name}: {ex.Message}");
        }
    }

    private bool OpenVoiceWebFallback(string target, string name)
    {
        var (url, label) = target switch
        {
            "discord" => ("https://discord.com/app", "Discord Web"),
            "telegram" => ("https://web.telegram.org/", "Telegram Web"),
            "spotify" => ("https://open.spotify.com/", "Spotify Web"),
            "steam" => ("https://store.steampowered.com/", "сайт Steam"),
            _ => ((string?)null, ""),
        };
        if (url == null) return false;

        var opened = SafeShell.OpenUrl(url);
        SetVoiceStatus(opened
            ? $"{name} не запустилось — открываю {label}."
            : $"Не удалось открыть {label}.");
        return true;
    }

    private bool TryOpenSteamProtocol()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://open/main")
            {
                UseShellExecute = true,
            });
            SetVoiceStatus("Запускаю Steam…");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void VoiceVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingsLoading || !IsLoaded) return;

        _settings.GeminiVoiceVolume = (int)Math.Round(VoiceVolumeSlider.Value);
        VoiceVolumeValue.Text = $"{_settings.GeminiVoiceVolume}%";
        _settings.Save();
        _voiceAssistant?.SetOutputVolume(_settings.GeminiVoiceVolume);
    }
}
