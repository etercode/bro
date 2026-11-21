using System.IO;
using Microsoft.Web.WebView2.Core;
using Bro.AdBlock;

namespace Bro.Core;

/// <summary>Things every window shares: saved data, the ad blocker and the Chromium environment.</summary>
static class Services
{
    /// <summary>%LOCALAPPDATA%\Bro, or BRO_DATA_DIR if set (handy for testing with a clean profile).</summary>
    public static readonly string DataDir = ResolveDataDir();

    /// <summary>The browser used to be called milo: move its data folder over the first time.</summary>
    static string ResolveDataDir()
    {
        if (Environment.GetEnvironmentVariable("BRO_DATA_DIR") is { Length: > 0 } custom) return custom;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, "Bro");
        var old = Path.Combine(local, "Milo");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { Directory.Move(old, dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; } // in use: try again next start
        }
        return dir;
    }

    public static Settings Settings { get; private set; } = new();
    public static BookmarkStore Bookmarks { get; private set; } = null!;
    public static HistoryStore History { get; private set; } = null!;
    public static FaviconCache Favicons { get; private set; } = null!;
    public static DownloadManager Downloads { get; private set; } = null!;
    public static AdBlocker AdBlock { get; private set; } = null!;

    public static readonly List<MainWindow> Windows = new();

    /// <summary>Raised after any setting changes.</summary>
    public static event Action? SettingsChanged;

    static Task<CoreWebView2Environment>? _env;

    static string DataFile(string name) => Path.Combine(DataDir, name);

    public static void Init()
    {
        Settings = JsonFile.Load(DataFile("settings.json"), () => new Settings());
        Bookmarks = new BookmarkStore(DataFile("bookmarks.json"));
        History = new HistoryStore(DataFile("history.json"));
        Favicons = new FaviconCache(DataFile("favicons.json"));
        Downloads = new DownloadManager(DataFile("downloads.json"));
        AdBlock = new AdBlocker(Path.Combine(DataDir, "filters"));
        _ = AdBlock.StartAsync();
    }

    /// <summary>One Chromium environment (and so one set of processes) for every window.</summary>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "UserData"));

    public static void SaveSettings()
    {
        JsonFile.Save(DataFile("settings.json"), Settings);
        SettingsChanged?.Invoke();
    }

    /// <summary>Takes settings from a backup, keeping this PC's download folder.</summary>
    public static void ReplaceSettings(Settings incoming)
    {
        incoming.DownloadFolder = Settings.DownloadFolder;
        Settings = incoming;
        SaveSettings();
    }

    public static void Flush()
    {
        History.Flush();
        Favicons.Flush();
    }

    // ── session (tabs to reopen with "continue where you left off") ──

    sealed class Session { public List<string> Urls { get; set; } = new(); public int Active { get; set; } }

    public static void SaveSession(IEnumerable<string> urls, int active) =>
        JsonFile.Save(DataFile("session.json"), new Session { Urls = urls.ToList(), Active = active });

    public static (List<string> Urls, int Active) LoadSession()
    {
        var s = JsonFile.Load(DataFile("session.json"), () => new Session());
        return (s.Urls, s.Active);
    }
}
