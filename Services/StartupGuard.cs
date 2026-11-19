using System.IO;

namespace DynamicIsland.Services;

/// <summary>
/// "The island didn't open": a marker file lives from the start until the island has been up for a few seconds
/// (or was closed normally). If it is still there at the next start, the last start died or hung before that —
/// then light graphics (no glass, glow or effects) are switched on, which is what a person would do by hand.
/// </summary>
public static class StartupGuard
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "startup.pending");

    /// <summary>The previous start never got as far as "the island is up".</summary>
    public static bool PreviousFailed { get; private set; }

    public static void Begin()
    {
        try
        {
            PreviousFailed = File.Exists(FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, DateTime.UtcNow.ToString("o"));
        }
        catch { }
    }

    /// <summary>The island is up (or was closed on purpose): this start counts as good.</summary>
    public static void Done()
    {
        try { File.Delete(FilePath); } catch { }
    }
}
