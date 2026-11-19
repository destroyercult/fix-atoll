using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>
/// Long jobs of other programs, read from what they already show everyone: a percentage in the window title
/// ("45% complete" of Explorer's copy window, "45% Extracting" of 7-Zip, WinRAR, encoders, backup tools…).
/// Only titles are read; nothing is sent to or changed in those programs.
/// </summary>
public sealed partial class ProgressWatcher
{
    public sealed record Job(IntPtr Window, string App, int Percent, DateTime Since, int First);

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);

    // browsers put page titles there ("50% off…"): never a job
    private static readonly string[] Skip =
        ["chrome", "msedge", "firefox", "opera", "brave", "browser", "vivaldi", "iexplore", "telegram", "discord", "dynamicisland", "atoll"];

    [GeneratedRegex(@"^\s*\D{0,24}?(\d{1,3})(?:[.,]\d+)?\s?%|(\d{1,3})(?:[.,]\d+)?\s?%\s*$")]
    private static partial Regex Percent();

    private readonly Dictionary<IntPtr, Job> _jobs = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly uint _self = (uint)Environment.ProcessId;

    /// <summary>The job to show (the one that started last), or null.</summary>
    public Job? Current { get; private set; }

    /// <summary>A job that reached the end and closed its window: its program name.</summary>
    public event Action<string>? Finished;

    /// <summary>Screenshots and tests: pretend a job is running (null: none).</summary>
    public void Demo(string? app, int percent) => Current = app == null ? null : new Job(IntPtr.Zero, app, percent, DateTime.Now, 0);

    public void Poll()
    {
        var found = new Dictionary<IntPtr, (string App, int Pct)>();
        var sb = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len < 2 || len > 200) return true;
            sb.Clear();
            Native.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.IndexOf('%') < 0) return true;
            var m = Percent().Match(title);
            if (!m.Success) return true;
            int pct = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (pct > 100) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == _self) return true;
            var app = AppName(pid);
            if (app.Length == 0 || Skip.Any(s => app.Contains(s, StringComparison.OrdinalIgnoreCase))) return true;
            found[h] = (app, pct);
            return true;
        }, IntPtr.Zero);

        var now = DateTime.Now;
        foreach (var gone in _jobs.Keys.Where(k => !found.ContainsKey(k)).ToList())
        {
            var j = _jobs[gone];
            _jobs.Remove(gone);
            // it was followed for a while and nearly done when the window went away: it finished (not cancelled early)
            if (j.Percent >= 90 && j.Percent != j.First && now - j.Since > TimeSpan.FromSeconds(4)) Finished?.Invoke(j.App);
        }
        foreach (var (h, f) in found)
            _jobs[h] = _jobs.TryGetValue(h, out var old) ? old with { Percent = f.Pct } : new Job(h, f.App, f.Pct, now, f.Pct);
        if (_names.Count > 200) _names.Clear();

        // a number that never moves is not a job (a document called "100% cotton"): it must change first
        Current = _jobs.Values.Where(j => j.Percent != j.First)
                              .OrderByDescending(j => j.Since).FirstOrDefault();
    }

    private string AppName(uint pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            n = p.ProcessName;
        }
        catch { n = ""; }
        return _names[pid] = n;
    }

    /// <summary>"explorer" → what a person calls it.</summary>
    public static string Friendly(string process) => process.ToLowerInvariant() switch
    {
        "explorer" => L.T("Файлы"),
        "7zg" or "7zfm" or "7z" => "7-Zip",
        "winrar" => "WinRAR",
        "handbrake" => "HandBrake",
        "steam" or "steamwebhelper" => "Steam",
        _ => process.Length > 0 ? char.ToUpper(process[0]) + process[1..] : process,
    };
}
