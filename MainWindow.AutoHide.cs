using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Auto-hide: with the switch on (Settings → Behavior), the island folds away by itself — the same movement as the
/// "hide" arrow — once the cursor has stayed away from it for a few seconds and nothing is going on. The cursor
/// touching the top edge of the screen where the island was brings it back (the little bar can also be clicked).
/// </summary>
public partial class MainWindow
{
    private const double AutoHideAfterSeconds = 5;
    private bool _autoHidden;                       // folded away by this switch, not by the arrow
    private DateTime _autoHideIdleSince = DateTime.UtcNow;

    /// <summary>Every 250 ms, from OnTick.</summary>
    private void WatchAutoHide()
    {
        if (!IsLoaded || _hwnd == IntPtr.Zero) return;
        if (!_hidden) _autoHidden = false;
        var now = DateTime.UtcNow;

        if (!_settings.AutoHide)
        {
            if (_autoHidden) ShowIsland();          // the switch went off while the island was folded away: bring it back
            _autoHideIdleSince = now;
            return;
        }

        Native.GetCursorPos(out var cursor);

        if (_hidden)
        {
            // folded away by itself: it comes back when the cursor touches the top edge, where the island was
            if (_autoHidden && Math.Abs(cursor.Y - MonitorTop() * Dpi) <= 3 && Math.Abs(cursor.X - (Left + Width / 2) * Dpi) <= 220 * Dpi)
            {
                _autoHideIdleSince = now;
                ShowIsland();
            }
            return;
        }

        // something is going on: open, a notice, typing, recording, a drag, a game or full-screen program…
        bool busy = _mode != Mode.Compact || _dragging || _editMode || _recording.IsRecording || _gameMode || _fullscreenHidden
                    || Typing || _liveDemo || _demoMedia != null;
        bool near;
        try
        {
            var tl = Island.PointToScreen(new Point(0, 0));
            double k = Dpi * UiScale, pad = 40 * Dpi;
            double w = Island.ActualWidth * k, h = Island.ActualHeight * k;
            near = cursor.X >= tl.X - pad && cursor.X <= tl.X + w + pad && cursor.Y >= tl.Y - pad && cursor.Y <= tl.Y + h + pad;
        }
        catch { near = true; }

        if (busy || near || Island.IsMouseOver) { _autoHideIdleSince = now; return; }
        if ((now - _autoHideIdleSince).TotalSeconds < AutoHideAfterSeconds) return;

        _autoHidden = true;
        _autoHideIdleSince = now;
        HideIsland(animate: true, auto: true);
    }
}
