using Microsoft.Web.WebView2.Core;
using Bro.AdBlock;
using Bro.Core;

namespace Bro;

public partial class MainWindow
{
    void SetupAdBlock(BrowserTab tab, CoreWebView2 core)
    {
        core.WebResourceRequested += (_, e) => OnResourceRequested(tab, e);
        core.DOMContentLoaded += (_, _) => ApplySiteHiding(tab, core);
        ApplyAdBlock(tab);
    }

    static bool AdBlockOn => Services.Settings.AdBlock && Services.AdBlock.Engine != null;

    /// <summary>Called when a page starts loading: works out whether this page gets blocking at all.</summary>
    void BeginPageForAdBlock(BrowserTab tab, string url)
    {
        tab.PageUrl = url;
        tab.PageHost = !IsPagesUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? u.Host.ToLowerInvariant() : "";
        tab.Blocked = 0;
        tab.BlockedHosts.Clear();
        tab.PageAllowed = tab.PageHost.Length == 0 ||
                          AdBlocker.IsPaused(tab.PageHost, Services.Settings.AdBlockPausedSites) ||
                          Services.AdBlock.Engine?.IsPageAllowed(url) == true;
    }

    /// <summary>Turns request filtering and the hiding script on/off for a tab to match the settings.</summary>
    async void ApplyAdBlock(BrowserTab tab)
    {
        if (tab.View.CoreWebView2 is not { } core) return;
        try
        {
            bool on = AdBlockOn;
            if (on && !tab.AdFilterOn)
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                                                   CoreWebView2WebResourceRequestSourceKinds.All);
                tab.AdFilterOn = true;
            }
            else if (!on && tab.AdFilterOn)
            {
                core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                                                      CoreWebView2WebResourceRequestSourceKinds.All);
                tab.AdFilterOn = false;
            }

            if (tab.AdScriptId != null)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(tab.AdScriptId);
                tab.AdScriptId = null;
            }
            if (!on || Services.AdBlock.DocumentScript(Services.Settings.AdBlockPausedSites) is not { } script) return;

            var version = ++tab.AdScriptVersion;
            var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (version != tab.AdScriptVersion || !_tabs.Contains(tab) || tab.AdScriptId != null)
                core.RemoveScriptToExecuteOnDocumentCreated(id); // a newer update won the race
            else tab.AdScriptId = id;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // Tab closed meanwhile.
        }
    }

    void OnResourceRequested(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Services.Settings.AdBlock || tab.PageAllowed || tab.PageHost.Length == 0) return;
        var engine = Services.AdBlock.Engine;
        if (engine == null) return;

        var url = e.Request.Uri;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("ws", StringComparison.OrdinalIgnoreCase)) return;
        // Never block the page itself, or frames from the same site
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document &&
            FilterEngine.SiteOf(FilterEngine.HostOf(url)) == FilterEngine.SiteOf(tab.PageHost)) return;

        if (!engine.ShouldBlock(url, AdBlocker.TypeOf(e.ResourceContext), tab.PageHost)) return;
        e.Response = _env!.CreateWebResourceResponse(null, 403, "Blocked by bro!", "");
        tab.Blocked++;
        var host = FilterEngine.HostOf(url);
        tab.BlockedHosts[host] = tab.BlockedHosts.TryGetValue(host, out var n) ? n + 1 : 1;
        if (tab == _active) QueueState();
    }

    void ApplySiteHiding(BrowserTab tab, CoreWebView2 core)
    {
        if (!AdBlockOn || tab.PageAllowed || tab.PageHost.Length == 0) return;
        var css = Services.AdBlock.Engine!.SiteCss(core.Source);
        if (css.Length > 0) _ = core.ExecuteScriptAsync(AdBlocker.SiteScript(css));
    }

    /// <summary>"off"/"paused"/"on" for the shield button, or "none" where blocking doesn't apply.</summary>
    static string ShieldState(BrowserTab? tab)
    {
        if (tab == null || tab.PageHost.Length == 0) return "none";
        if (!Services.Settings.AdBlock) return "off";
        return AdBlocker.IsPaused(tab.PageHost, Services.Settings.AdBlockPausedSites) ? "paused" : "on";
    }

    /// <summary>What the shield's dropdown shows for the current tab.</summary>
    void SendAdBlockPanel()
    {
        if (!_uiReady || UiView.CoreWebView2 == null) return;
        var tab = _active;
        var panel = new
        {
            type = "adblockPanel",
            state = ShieldState(tab),
            site = tab == null ? "" : FilterEngine.SiteOf(tab.PageHost),
            blocked = tab?.Blocked ?? 0,
            hosts = (tab?.BlockedHosts ?? new Dictionary<string, int>()).OrderByDescending(h => h.Value).Take(40)
                        .Select(h => new { host = h.Key, count = h.Value }).ToArray(),
            status = Services.AdBlock.Status,
        };
        UiView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(panel, JsonOpts));
    }

    /// <summary>Shield button: pause or resume blocking on the current site, then reload it.</summary>
    void ToggleShield()
    {
        if (_active == null || _active.PageHost.Length == 0) return;
        var paused = Services.Settings.AdBlockPausedSites;
        if (!Services.Settings.AdBlock)
        {
            Services.Settings.AdBlock = true;
            paused.RemoveAll(s => FilterEngine.IsSameOrSubdomain(_active.PageHost, s));
        }
        else if (AdBlocker.IsPaused(_active.PageHost, paused))
            paused.RemoveAll(s => FilterEngine.IsSameOrSubdomain(_active.PageHost, s));
        else
            paused.Add(FilterEngine.SiteOf(_active.PageHost));
        Services.SaveSettings();
        _active.View.CoreWebView2?.Reload();
    }
}
