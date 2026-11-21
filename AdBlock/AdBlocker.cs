using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Bro.AdBlock;

/// <summary>
/// Keeps EasyList + EasyPrivacy on disk (refreshed every few days), parses them in the background
/// and answers "block this request?" for every tab.
/// </summary>
sealed class AdBlocker(string folder)
{
    public static readonly (string Name, string File, string Url)[] Lists =
    {
        ("EasyList", "easylist.txt", "https://easylist.to/easylist/easylist.txt"),
        ("EasyPrivacy", "easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt"),
    };
    static readonly TimeSpan MaxListAge = TimeSpan.FromDays(4);

    volatile FilterEngine? _engine;
    bool _updating;

    public FilterEngine? Engine => _engine;
    public string Status { get; private set; } = "Loading filter lists…";
    public DateTime? ListsUpdated { get; private set; }
    public bool Updating => _updating;

    /// <summary>Raised on the UI thread when new rules are ready or the status changes.</summary>
    public event Action? Changed;

    /// <summary>Loads cached lists right away, then refreshes them if they're old or missing.</summary>
    public async Task StartAsync()
    {
        await LoadCachedAsync();
        var oldest = Lists.Select(l => Path.Combine(folder, l.File))
                          .Select(p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue).Min();
        if (DateTime.UtcNow - oldest > MaxListAge) await UpdateAsync();
    }

    async Task LoadCachedAsync()
    {
        var paths = Lists.Select(l => Path.Combine(folder, l.File)).Where(File.Exists).ToList();
        if (paths.Count == 0) { SetStatus("Downloading filter lists…"); return; }
        var engine = await Task.Run(() => FilterEngine.Parse(paths.SelectMany(SafeReadLines)));
        _engine = engine;
        ListsUpdated = paths.Select(File.GetLastWriteTime).Min();
        SetStatus($"{engine.NetworkRuleCount + engine.CosmeticRuleCount:N0} rules");
    }

    static IEnumerable<string> SafeReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    public async Task UpdateAsync()
    {
        if (_updating) return;
        _updating = true;
        SetStatus("Updating filter lists…");
        try
        {
            Directory.CreateDirectory(folder);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("bro-browser");
            foreach (var list in Lists)
            {
                var text = await http.GetStringAsync(list.Url);
                if (!text.TrimStart().StartsWith("[Adblock", StringComparison.OrdinalIgnoreCase)) continue; // not a filter list
                var path = Path.Combine(folder, list.File);
                await File.WriteAllTextAsync(path + ".tmp", text);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            _updating = false;
            await LoadCachedAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _updating = false;
            SetStatus(_engine == null ? "Couldn't download filter lists (offline?)" :
                                        $"{_engine.NetworkRuleCount + _engine.CosmeticRuleCount:N0} rules · update failed");
        }
    }

    void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public static ResourceType TypeOf(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => ResourceType.Subdocument, // main pages are never checked
        CoreWebView2WebResourceContext.Stylesheet => ResourceType.Stylesheet,
        CoreWebView2WebResourceContext.Image => ResourceType.Image,
        CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.TextTrack => ResourceType.Media,
        CoreWebView2WebResourceContext.Font => ResourceType.Font,
        CoreWebView2WebResourceContext.Script => ResourceType.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch or
            CoreWebView2WebResourceContext.EventSource => ResourceType.Xhr,
        CoreWebView2WebResourceContext.Websocket => ResourceType.WebSocket,
        CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => ResourceType.Ping,
        _ => ResourceType.Other,
    };

    public static bool IsPaused(string pageHost, IEnumerable<string> pausedSites) =>
        pausedSites.Any(s => FilterEngine.IsSameOrSubdomain(pageHost, s));

    /// <summary>
    /// Script run at the start of every document: adds the shared hiding stylesheet, except on sites
    /// that are paused or that the lists say shouldn't get generic hiding.
    /// </summary>
    public string? DocumentScript(IEnumerable<string> pausedSites)
    {
        var engine = _engine;
        if (engine == null) return null;
        var skip = pausedSites.Concat(engine.NoGenericHosts).ToArray();
        return $$"""
            (() => {
              try {
                const origins = location.ancestorOrigins;
                const top = origins && origins.length ? new URL(origins[origins.length - 1]).hostname : location.hostname;
                if (!/^https?:$/.test(location.protocol) && !(origins && origins.length)) return;
                const skip = {{JsonSerializer.Serialize(skip)}};
                if (skip.some((d) => top === d || top.endsWith('.' + d))) return;
                const sheet = new CSSStyleSheet();
                sheet.replaceSync({{JsonSerializer.Serialize(engine.GenericCss())}});
                document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
              } catch (e) {}
            })();
            """;
    }

    /// <summary>Script that adds one site's own hiding rules (run when its page has loaded).</summary>
    public static string SiteScript(string css) => $$"""
        (() => {
          try {
            const sheet = new CSSStyleSheet();
            sheet.replaceSync({{JsonSerializer.Serialize(css)}});
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
          } catch (e) {}
        })();
        """;
}
