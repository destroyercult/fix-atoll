using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// System tab → the volume of each program that makes sound (a game quieter than the voice chat, a browser
/// muted), without opening the Windows mixer.
/// </summary>
public partial class MainWindow
{
    private string _mixerKey = "";
    private bool _mixerLoading;

    /// <summary>Once a second while the System tab is open.</summary>
    private void RefreshMixer()
    {
        List<AudioService.AppSound> apps = _demoMedia != null
            ? [new("Spotify", 0.8f, false), new("Discord", 0.6f, false), new("chrome", 0.45f, false)]   // screenshots show nobody's real programs
            : _audio.AppSounds();
        MixerTile.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join("|", apps.Select(a => a.Name));
        if (key != _mixerKey)
        {
            _mixerKey = key;
            MixerRows.Children.Clear();
            foreach (var app in apps)
            {
                var name = app.Name;
                var mute = new Button { Style = (Style)FindResource("IconButton"), Width = 30, Height = 30, FontSize = 13, Tag = name, ToolTip = L.T("Выключить или включить звук") };
                var label = new TextBlock { Text = ProgressWatcher.Friendly(name), FontSize = 13, Width = 132, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var value = new TextBlock { Style = (Style)FindResource("Muted"), Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Style = (Style)FindResource("SoftSlider"), Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Tag = name };
                slider.ValueChanged += (_, e) =>
                {
                    value.Text = $"{e.NewValue:0}%";
                    if (_mixerLoading || _demoMedia != null) return;
                    _audio.SetAppVolume(name, (float)(e.NewValue / 100));
                };
                mute.Click += (_, _) =>
                {
                    if (_demoMedia == null) _audio.ToggleAppSoundMute(name);
                    RefreshMixer();
                };
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                DockPanel.SetDock(mute, Dock.Left); DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(mute); row.Children.Add(label); row.Children.Add(value); row.Children.Add(slider);
                MixerRows.Children.Add(row);
            }
            Remeasure();
        }

        // what the programs are set to now (unless a slider is being dragged)
        _mixerLoading = true;
        for (int i = 0; i < apps.Count && i < MixerRows.Children.Count; i++)
        {
            var row = (DockPanel)MixerRows.Children[i];
            var mute = (Button)row.Children[0];
            var slider = (Slider)row.Children[3];
            mute.Content = apps[i].Muted ? "" : "";
            mute.Opacity = apps[i].Muted ? 0.55 : 1;
            if (!slider.IsMouseCaptureWithin) slider.Value = Math.Round(apps[i].Volume * 100);
        }
        _mixerLoading = false;
    }
}
