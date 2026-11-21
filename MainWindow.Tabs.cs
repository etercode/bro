using System.Windows;
using Bro.AdBlock;
using Bro.Core;

namespace Bro;

/// <summary>Grouping tabs by site, dragging them into a new order, and dragging one out into its own window.</summary>
public partial class MainWindow
{
    static string SiteKey(string url) =>
        !IsPagesUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? FilterEngine.SiteOf(u.Host.ToLowerInvariant()) : "";

    /// <summary>When a tab lands on a site that's already open, it moves next to that site's other tabs.</summary>
    void UpdateTabSite(BrowserTab tab)
    {
        var site = SiteKey(tab.Url);
        if (site == tab.Site) return;
        tab.Site = site;
        if (!Services.Settings.GroupTabs || site.Length == 0) return;

        var index = _tabs.IndexOf(tab);
        var lastSame = _tabs.FindLastIndex(t => t != tab && t.Site == site);
        if (lastSame < 0) return;
        var firstSame = _tabs.FindIndex(t => t != tab && t.Site == site);
        if (index >= firstSame - 1 && index <= lastSame + 1) return; // already next to the group
        _tabs.RemoveAt(index);
        _tabs.Insert(index < lastSame ? lastSame : lastSame + 1, tab);
        QueueState();
    }

    void MoveTab(int id, int newIndex)
    {
        if (FindTab(id) is not { } tab) return;
        _tabs.Remove(tab);
        _tabs.Insert(Math.Clamp(newIndex, 0, _tabs.Count), tab);
        QueueState();
    }

    /// <summary>A tab dropped outside the window opens in a new window there (the page reloads).</summary>
    void DetachTab(int id)
    {
        if (FindTab(id) is not { } tab || _tabs.Count < 2) return;
        var url = tab.Url;
        var index = _tabs.IndexOf(tab);
        _tabs.RemoveAt(index);
        TabHost.Children.Remove(tab.View);
        tab.View.Dispose();
        if (_active == tab) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else QueueState();

        // Open where the tab was dropped (cursor is in physical pixels; WPF uses device-independent units)
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var (screenX, screenY) = Native.CursorPosition();
        var w = new MainWindow(IsPrivate, new[] { url })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = WindowState == WindowState.Normal ? Width : 1200,
            Height = WindowState == WindowState.Normal ? Height : 800,
        };
        w.Left = screenX / scale - 120;
        w.Top = screenY / scale - 24;
        w.Show();
        w.Activate();
    }
}
