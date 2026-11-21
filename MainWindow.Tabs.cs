using System.Windows;
using Bro.AdBlock;
using Bro.Core;

namespace Bro;

/// <summary>
/// Grouping tabs by site, and dragging tabs: to a new position, out into their own window, or into
/// another bro! window. Like Chrome: drop on another window to move the tab there, drop on empty desktop
/// for a new window, and dragging a window's only tab moves the whole window.
/// </summary>
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

    /// <summary>Vertical centres of the tabs in the UI (reported by the UI), to place a tab dropped from another window.</summary>
    List<double> _tabMids = new();

    double Scale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    /// <summary>A tab was released outside this window's UI: decide where it goes.</summary>
    void DropTabOutside(int id, (int X, int Y)? at = null)
    {
        if (FindTab(id) is not { } tab) return;
        var (x, y) = at ?? Native.CursorPosition();
        var under = Native.TopLevelWindowAt(x, y);
        var target = Services.Windows.FirstOrDefault(w => w.Hwnd == under);

        if (target == this) return; // still over this window after all
        if (target != null && target.IsPrivate == IsPrivate && !target._closing)
        {
            MoveTabToWindow(tab, target, y);
            return;
        }
        if (_tabs.Count > 1) DetachTab(tab, x, y);
        else MoveWindowTo(x, y); // the only tab: drag the whole window, like Chrome
    }

    /// <summary>Takes a tab out of this window without closing the window (unless it was the last tab).</summary>
    void RemoveTabForMove(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        _tabs.RemoveAt(index);
        TabHost.Children.Remove(tab.View);
        tab.View.Dispose();
        if (_tabs.Count == 0) { Close(); return; }
        if (_active == tab) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else QueueState();
    }

    void MoveTabToWindow(BrowserTab tab, MainWindow target, int screenY)
    {
        var url = tab.Url;
        RemoveTabForMove(tab);
        target.ReceiveTab(url, screenY);
    }

    /// <summary>Another window dropped a tab on us: open it at the position it was dropped (the page reloads).</summary>
    async void ReceiveTab(string url, int screenY)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var y = UiView.PointFromScreen(new Point(0, screenY)).Y;
        var index = _tabMids.Count(m => m < y);
        var tab = await CreateTabAsync(url);
        if (_tabs.Contains(tab)) MoveTab(tab.Id, index);
    }

    /// <summary>A tab dropped on empty desktop opens in a new window there (the page reloads).</summary>
    void DetachTab(BrowserTab tab, int screenX, int screenY)
    {
        var url = tab.Url;
        RemoveTabForMove(tab);

        // Cursor is in physical pixels; WPF positions windows in device-independent units
        var scale = Scale;
        var w = new MainWindow(IsPrivate, new[] { url })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = WindowState == WindowState.Normal ? Width : 1200,
            Height = WindowState == WindowState.Normal ? Height : 800,
        };
        (w.Left, w.Top) = PlaceUnderCursor(screenX, screenY, w.Width, w.Height, scale);
        w.Show();
        w.Activate();
    }

    void MoveWindowTo(int screenX, int screenY)
    {
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        (Left, Top) = PlaceUnderCursor(screenX, screenY, Width, Height, Scale);
        Activate();
    }

    /// <summary>
    /// The tab rail is on the right, so the window goes left of the cursor, with the tab under it,
    /// kept inside the monitor's visible area.
    /// </summary>
    static (double Left, double Top) PlaceUnderCursor(int screenX, int screenY, double width, double height, double scale)
    {
        var area = Native.WorkAreaAt(screenX, screenY);
        double left = screenX / scale - width + 30, top = screenY / scale - 80;
        left = Math.Clamp(left, area.Left / scale, Math.Max(area.Left / scale, area.Right / scale - width));
        top = Math.Clamp(top, area.Top / scale, Math.Max(area.Top / scale, area.Bottom / scale - height));
        return (left, top);
    }
}
