using System.IO;
using System.Text.Json;

namespace Bro.Core;

/// <summary>Everything on the settings page. Saved to settings.json.</summary>
sealed class Settings
{
    public string SearchEngine { get; set; } = "duckduckgo";
    public string Startup { get; set; } = "newtab";          // "newtab" | "restore"
    public string Accent { get; set; } = "#1e66f5"; // the owl's blue
    public bool NewTabClock { get; set; } = true;
    public bool NewTabBookmarks { get; set; } = true;
    public bool GroupTabs { get; set; } = true;         // tabs from the same site sit together
    public string DownloadFolder { get; set; } = "";         // "" = the Windows Downloads folder
    public bool AskWhereToSave { get; set; }
    public bool AdBlock { get; set; } = true;
    public List<string> AdBlockPausedSites { get; set; } = new();
    public string TrackingPrevention { get; set; } = "balanced"; // none | basic | balanced | strict

    public static readonly Dictionary<string, (string Name, string Url)> SearchEngines = new()
    {
        ["duckduckgo"] = ("DuckDuckGo", "https://duckduckgo.com/?q="),
        ["google"] = ("Google", "https://www.google.com/search?q="),
        ["bing"] = ("Bing", "https://www.bing.com/search?q="),
        ["brave"] = ("Brave Search", "https://search.brave.com/search?q="),
        ["startpage"] = ("Startpage", "https://www.startpage.com/do/search?q="),
    };

    public static readonly string[] Accents =
        { "#1e66f5", "#0fb5d4", "#9b6cf0", "#e0457b", "#e2643c", "#5fae4b", "#d9a23a" };

    [System.Text.Json.Serialization.JsonIgnore] public string SearchName => SearchEngines.TryGetValue(SearchEngine, out var e) ? e.Name : "DuckDuckGo";
    [System.Text.Json.Serialization.JsonIgnore] public string SearchUrl => SearchEngines.TryGetValue(SearchEngine, out var e) ? e.Url : SearchEngines["duckduckgo"].Url;

    [System.Text.Json.Serialization.JsonIgnore] public string EffectiveDownloadFolder =>
        Directory.Exists(DownloadFolder) ? DownloadFolder : KnownFolders.Downloads;

    /// <summary>Applies one change from the settings page. Returns false for unknown keys or bad values.</summary>
    public bool Set(string key, JsonElement value)
    {
        bool Bool() => value.ValueKind == JsonValueKind.True;
        string Str() => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        switch (key)
        {
            case "searchEngine" when SearchEngines.ContainsKey(Str()): SearchEngine = Str(); return true;
            case "startup" when Str() is "newtab" or "restore": Startup = Str(); return true;
            case "accent" when Accents.Contains(Str()): Accent = Str(); return true;
            case "newTabClock" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: NewTabClock = Bool(); return true;
            case "newTabBookmarks" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: NewTabBookmarks = Bool(); return true;
            case "groupTabs" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: GroupTabs = Bool(); return true;
            case "askWhereToSave" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: AskWhereToSave = Bool(); return true;
            case "adBlock" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: AdBlock = Bool(); return true;
            case "trackingPrevention" when Str() is "none" or "basic" or "balanced" or "strict": TrackingPrevention = Str(); return true;
            default: return false;
        }
    }

    public object ToJson() => new
    {
        searchEngine = SearchEngine,
        searchEngines = SearchEngines.Select(e => new { id = e.Key, name = e.Value.Name }),
        startup = Startup,
        accent = Accent,
        accents = Accents,
        newTabClock = NewTabClock,
        newTabBookmarks = NewTabBookmarks,
        groupTabs = GroupTabs,
        downloadFolder = EffectiveDownloadFolder,
        askWhereToSave = AskWhereToSave,
        adBlock = AdBlock,
        adBlockPausedSites = AdBlockPausedSites,
        trackingPrevention = TrackingPrevention,
    };
}

static class KnownFolders
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out string path);

    public static string Downloads
    {
        get
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            if (SHGetKnownFolderPath(ref id, 0, 0, out var path) == 0 && Directory.Exists(path)) return path;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
