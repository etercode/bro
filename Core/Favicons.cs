namespace Bro.Core;

/// <summary>Remembers each site's icon (as a small PNG data: URL) for bookmarks, history and suggestions.</summary>
sealed class FaviconCache
{
    const int MaxSites = 500;
    const int MaxIconLength = 24_000; // skip unusually large icons

    readonly string _path;
    readonly Dictionary<string, Entry> _icons;
    readonly Debouncer _save;

    sealed class Entry { public string Icon { get; set; } = ""; public long Used { get; set; } }

    public FaviconCache(string path)
    {
        _path = path;
        _icons = JsonFile.Load(path, () => new Dictionary<string, Entry>());
        _save = new Debouncer(TimeSpan.FromSeconds(3), Save);
    }

    public event Action? Changed;

    static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";

    public string Get(string url) => _icons.TryGetValue(Host(url), out var e) ? e.Icon : "";

    public void Set(string url, string icon)
    {
        var host = Host(url);
        if (host.Length == 0 || icon.Length == 0 || icon.Length > MaxIconLength) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_icons.TryGetValue(host, out var e) && e.Icon == icon) { e.Used = now; return; }
        _icons[host] = new Entry { Icon = icon, Used = now };
        if (_icons.Count > MaxSites)
            foreach (var old in _icons.OrderBy(x => x.Value.Used).Take(_icons.Count - MaxSites).ToList())
                _icons.Remove(old.Key);
        _save.Request();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _icons.Clear();
        _save.Request();
    }

    void Save() => JsonFile.Save(_path, _icons);
    public void Flush() => _save.Flush();
}
