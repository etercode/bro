using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Bro.Core;

namespace Bro;

public partial class MainWindow : Window
{
    const string UiHost = "ui.bro";
    const string PagesHost = "pages.bro";
    const string PagesRoot = "https://pages.bro/";
    const double PageCornerRadius = 12;
    const double ResizeStrip = 4; // uncovered edge so the window stays resizable

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    static readonly System.Drawing.Color PageDark = System.Drawing.Color.FromArgb(0x13, 0x11, 0x16);

    readonly List<BrowserTab> _tabs = new();
    readonly Stack<string> _closedTabs = new();
    readonly IReadOnlyList<string> _startUrls;
    readonly int _startActive;
    CoreWebView2Environment? _env;
    CoreWebView2ControllerOptions? _tabOptions; // private windows: InPrivate profile
    BrowserTab? _active;
    int _nextTabId = 1;
    bool _uiReady, _stateQueued, _fullscreen, _closing;
    bool _downloadFlash;
    WindowState _stateBeforeFullscreen;
    Rect _viewport = Rect.Empty;

    public bool IsPrivate { get; }

    string NewTabUrl => PagesRoot + (IsPrivate ? "newtab.html?private" : "newtab.html");

    nint Hwnd => new WindowInteropHelper(this).Handle;

    public MainWindow(bool isPrivate = false, IReadOnlyList<string>? startUrls = null, int startActive = 0)
    {
        IsPrivate = isPrivate;
        _startUrls = startUrls is { Count: > 0 } ? startUrls : Array.Empty<string>();
        _startActive = startActive;
        InitializeComponent();
        BuildMenu();

        SourceInitialized += (_, _) =>
        {
            Native.StyleWindow(Hwnd);
            HwndSource.FromHwnd(Hwnd).AddHook(WndProc);
        };
        Loaded += async (_, _) => await InitializeAsync();
        StateChanged += (_, _) => { ApplyWindowStateLayout(); QueueState(); };
        Deactivated += (_, _) => { MenuPopup.IsOpen = false; CloseSuggestions(); ClosePopovers(); };
        LocationChanged += (_, _) => CloseSuggestions();
        Root.SizeChanged += (_, _) => { if (_fullscreen) LayoutTabHost(); };
        Closing += (_, _) => OnClosing();
        Closed += (_, _) => OnClosed();

        Services.Windows.Add(this);
        Services.Bookmarks.Changed += OnBookmarksChanged;
        Services.History.Changed += OnHistoryChanged;
        Services.Downloads.Changed += OnDownloadsChanged;
        Services.AdBlock.Changed += OnAdBlockChanged;
        Services.SettingsChanged += OnSettingsChanged;
    }

    // ───────────────────────── startup / shutdown ─────────────────────────

    async Task InitializeAsync()
    {
        try
        {
            _env = await Services.GetEnvironmentAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("bro! needs the Microsoft Edge WebView2 Runtime.\n\n" +
                            "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start bro! again.",
                            "bro!", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        if (IsPrivate)
        {
            _tabOptions = _env.CreateCoreWebView2ControllerOptions();
            _tabOptions.IsInPrivateModeEnabled = true;
            _tabOptions.ProfileName = "private";
        }

        UiView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B);
        await UiView.EnsureCoreWebView2Async(_env);
        var ui = UiView.CoreWebView2;
        ui.SetVirtualHostNameToFolderMapping(UiHost, Path.Combine(AppContext.BaseDirectory, "ui"),
                                             CoreWebView2HostResourceAccessKind.Deny);
        var s = ui.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false; // F5 / Ctrl+R etc. must not reload the UI itself
        s.IsZoomControlEnabled = false;
        s.IsStatusBarEnabled = false;
#if !DEBUG
        s.AreDevToolsEnabled = false;
#endif
        // The UI may only ever show our own files, and only it may talk to the C# side.
        ui.NavigationStarting += (_, e) => { if (!IsUiUrl(e.Uri)) e.Cancel = true; };
        ui.NewWindowRequested += (_, e) => e.Handled = true;
        ui.WebMessageReceived += OnUiMessage;
        ApplyProfileSettings(ui.Profile);
        Native.SendToBottom(UiView.Handle);
        ui.Navigate($"https://{UiHost}/index.html");

        if (_startUrls.Count == 0) await CreateTabAsync(NewTabUrl);
        else
        {
            foreach (var url in _startUrls) await CreateTabAsync(url, activate: false);
            Activate(_tabs[Math.Clamp(_startActive, 0, _tabs.Count - 1)]);
        }
    }

    void OnClosing()
    {
        _closing = true;
        // The last normal window remembers its tabs for "continue where you left off".
        if (!IsPrivate && !Services.Windows.Any(w => w != this && !w.IsPrivate)) SaveSession();
    }

    void OnClosed()
    {
        Services.Windows.Remove(this);
        Services.Bookmarks.Changed -= OnBookmarksChanged;
        Services.History.Changed -= OnHistoryChanged;
        Services.Downloads.Changed -= OnDownloadsChanged;
        Services.AdBlock.Changed -= OnAdBlockChanged;
        Services.SettingsChanged -= OnSettingsChanged;
        foreach (var t in _tabs) t.View.Dispose();
        _tabs.Clear();
    }

    public void SaveSession()
    {
        var urls = _tabs.Select(t => t.Url).Where(u => !IsNewTab(u) && u.Length > 0).ToList();
        var active = _active == null ? 0 : Math.Max(0, urls.IndexOf(_active.Url));
        Services.SaveSession(urls, active);
    }

    static void OpenWindow(bool isPrivate)
    {
        var w = new MainWindow(isPrivate) { WindowStartupLocation = WindowStartupLocation.Manual };
        if (Application.Current.MainWindow is { } current && current != w)
        {
            w.Left = current.Left + 32;
            w.Top = current.Top + 32;
        }
        w.Show();
        w.Activate();
    }

    void QuitAll()
    {
        if (!IsPrivate) SaveSession();
        else Services.Windows.FirstOrDefault(w => !w.IsPrivate)?.SaveSession();
        Application.Current.Shutdown();
    }

    // ───────────────────────── tabs ─────────────────────────

    async Task<BrowserTab> CreateTabAsync(string? url, bool activate = true)
    {
        var view = new WebView2
        {
            DefaultBackgroundColor = url != null && IsPagesUrl(url) ? PageDark : System.Drawing.Color.White,
        };
        var tab = new BrowserTab(_nextTabId++, view) { Url = url ?? "", Loading = url != null };
        _tabs.Add(tab);
        TabHost.Children.Add(view);
        view.Visibility = Visibility.Collapsed;
        view.SizeChanged += (_, _) => ClipToRoundedCorners(view);
        if (activate) Activate(tab);
        else QueueState();

        try
        {
            await view.EnsureCoreWebView2Async(_env, _tabOptions);
        }
        catch when (!_tabs.Contains(tab))
        {
            return tab; // closed while it was starting up
        }
        if (!_tabs.Contains(tab)) return tab;

        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(PagesHost, Path.Combine(AppContext.BaseDirectory, "pages"),
                                               CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.IsStatusBarEnabled = true;
        if (IsPrivate) ApplyProfileSettings(core.Profile);

        core.NavigationStarting += (_, e) => OnNavigationStarting(tab, view, e);
        core.SourceChanged += (_, e) =>
        {
            tab.Url = core.Source;
            UpdateTabSite(tab);
            if (!e.IsNewDocument && !IsPrivate) Services.History.Add(core.Source, core.DocumentTitle);
            QueueState();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = core.DocumentTitle;
            if (!IsPrivate) Services.History.UpdateTitle(core.Source, core.DocumentTitle);
            QueueState();
        };
        core.NavigationCompleted += (_, e) =>
        {
            tab.Loading = false;
            if (e.IsSuccess && !IsPrivate) Services.History.Add(core.Source, core.DocumentTitle);
            QueueState();
        };
        core.HistoryChanged += (_, _) => QueueState();
        core.FaviconChanged += async (_, _) =>
        {
            tab.Favicon = await ReadFaviconAsync(core);
            if (!IsPrivate && tab.Favicon.Length > 0) Services.Favicons.Set(core.Source, tab.Favicon);
            QueueState();
        };
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested += (_, _) => Dispatcher.InvokeAsync(() => CloseTab(tab));
        core.DownloadStarting += (_, e) => OnDownloadStarting(tab, e);
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == _active || !core.ContainsFullScreenElement) SetFullscreen(core.ContainsFullScreenElement);
        };
        core.WebMessageReceived += (_, e) => OnPageMessage(core, e);
        SetupAdBlock(tab, core);
        SetupSound(tab, core);

        ClipToRoundedCorners(view);
        if (tab == _active) Native.BringToTop(view.Handle);
        if (url != null) core.Navigate(url);
        return tab;
    }

    void OnNavigationStarting(BrowserTab tab, WebView2 view, CoreWebView2NavigationStartingEventArgs e)
    {
        tab.Loading = true;
        view.DefaultBackgroundColor = IsPagesUrl(e.Uri) ? PageDark : System.Drawing.Color.White;
        BeginPageForAdBlock(tab, e.Uri);
        QueueState();
    }

    /// <summary>Reads the page's icon from Chromium (no extra download) as a PNG data: URL.</summary>
    static async Task<string> ReadFaviconAsync(CoreWebView2 core)
    {
        try
        {
            if (string.IsNullOrEmpty(core.FaviconUri)) return "";
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null) return "";
            using var png = new MemoryStream();
            await stream.CopyToAsync(png);
            return png.Length == 0 ? "" : "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        }
        catch (Exception)
        {
            return ""; // tab closed meanwhile, or the icon couldn't be decoded
        }
    }

    async Task NewTabAsync()
    {
        await CreateTabAsync(NewTabUrl);
        FocusAddressBar();
    }

    /// <summary>Links with target=_blank and window.open() become new tabs.</summary>
    async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var tab = await CreateTabAsync(null);
            if (tab.View.CoreWebView2 is { } core) e.NewWindow = core;
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    void Activate(BrowserTab tab)
    {
        if (_active != tab && _fullscreen) SetFullscreen(false);
        _active = tab;
        foreach (var t in _tabs)
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        Native.BringToTop(tab.View.Handle);
        ClipToRoundedCorners(tab.View);
        CloseSuggestions();
        QueueState();
    }

    /// <summary>Closing the last tab leaves a fresh new tab, so the window stays open.</summary>
    void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (_tabs.Count == 1)
        {
            if (IsNewTab(tab.Url)) { FocusAddressBar(); return; } // nothing to close
            _ = CreateTabAsync(NewTabUrl).ContinueWith(_ => FocusAddressBar(), TaskScheduler.FromCurrentSynchronizationContext());
            index = _tabs.IndexOf(tab);
        }
        if (!IsNewTab(tab.Url) && tab.Url.Length > 0) _closedTabs.Push(tab.Url);
        _tabs.RemoveAt(index);
        TabHost.Children.Remove(tab.View);
        tab.View.Dispose();

        if (_active == tab) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else QueueState();
    }

    void ReopenClosedTab()
    {
        if (_closedTabs.Count > 0) _ = CreateTabAsync(_closedTabs.Pop());
    }

    void CycleTab(int step)
    {
        if (_active == null || _tabs.Count < 2) return;
        var i = (_tabs.IndexOf(_active) + step + _tabs.Count) % _tabs.Count;
        Activate(_tabs[i]);
    }

    BrowserTab? FindTab(int id) => _tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>Shows bro://name: reuses a tab already showing it, or the current new tab.</summary>
    void OpenPage(string name)
    {
        var url = PagesRoot + name + ".html";
        if (_tabs.FirstOrDefault(t => t.Url.StartsWith(url, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            Activate(open);
            return;
        }
        if (_active != null && IsNewTab(_active.Url) && _active.View.CoreWebView2 is { } core) core.Navigate(url);
        else _ = CreateTabAsync(url);
    }

    // ───────────────────────── navigation ─────────────────────────

    void NavigateActive(string text)
    {
        CloseSuggestions();
        if (_active?.View.CoreWebView2 is not { } core) return;
        try { core.Navigate(ResolveInput(text)); }
        catch (ArgumentException) { core.Navigate(Services.Settings.SearchUrl + Uri.EscapeDataString(text)); }
        _active.View.Focus();
    }

    /// <summary>Turns address-bar text into a URL: a real address, a bare domain, bro://page, or a web search.</summary>
    string ResolveInput(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return NewTabUrl;

        if (text.StartsWith("bro://", StringComparison.OrdinalIgnoreCase))
        {
            var page = text["bro://".Length..].Trim('/').ToLowerInvariant();
            return page is "history" or "bookmarks" or "downloads" or "settings" ? PagesRoot + page + ".html" : NewTabUrl;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var abs) &&
            abs.Scheme is "http" or "https" or "file" or "about" or "edge")
            return abs.ToString();

        if (!text.Contains(' '))
        {
            var isLocal = text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                          text.StartsWith("127.0.0.1");
            var scheme = isLocal ? "http://" : "https://";
            if (Uri.TryCreate(scheme + text, UriKind.Absolute, out var guess) &&
                (isLocal || (guess.Host.Contains('.') && !guess.Host.EndsWith('.'))))
                return guess.ToString();
        }
        return Services.Settings.SearchUrl + Uri.EscapeDataString(text);
    }

    static bool IsUiUrl(string? url) =>
        url != null && url.StartsWith($"https://{UiHost}/", StringComparison.OrdinalIgnoreCase);

    static bool IsPagesUrl(string? url) =>
        url != null && url.StartsWith(PagesRoot, StringComparison.OrdinalIgnoreCase);

    static bool IsNewTab(string? url) =>
        string.IsNullOrEmpty(url) || url.StartsWith(PagesRoot + "newtab.html", StringComparison.OrdinalIgnoreCase);

    static string DisplayTitle(BrowserTab t)
    {
        if (!string.IsNullOrWhiteSpace(t.Title)) return t.Title;
        if (IsNewTab(t.Url)) return "New tab";
        return Uri.TryCreate(t.Url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : t.Url;
    }

    // ───────────────────────── bookmarks ─────────────────────────

    static bool CanBookmark(string? url) =>
        !IsPagesUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "file";

    void ToggleBookmark()
    {
        if (_active == null || !CanBookmark(_active.Url)) return;
        if (_active.Favicon.Length > 0) Services.Favicons.Set(_active.Url, _active.Favicon);
        Services.Bookmarks.Toggle(_active.Url, DisplayTitle(_active));
    }

    // ───────────────────────── downloads ─────────────────────────

    void OnDownloadStarting(BrowserTab tab, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; // Bro shows its own downloads instead of Edge's flyout
        if (!Services.Settings.AskWhereToSave)
        {
            StartTracking(e.DownloadOperation);
            return;
        }
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = Path.GetFileName(e.ResultFilePath),
                    InitialDirectory = Path.GetDirectoryName(e.ResultFilePath) ?? Services.Settings.EffectiveDownloadFolder,
                    Filter = "All files|*.*",
                };
                if (dialog.ShowDialog(this) == true)
                {
                    e.ResultFilePath = dialog.FileName;
                    StartTracking(e.DownloadOperation);
                }
                else e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    void StartTracking(CoreWebView2DownloadOperation op)
    {
        Services.Downloads.Track(op, IsPrivate);
        _downloadFlash = true;
        QueueState();
    }

    void ApplyProfileSettings(CoreWebView2Profile profile)
    {
        try
        {
            profile.DefaultDownloadFolderPath = Services.Settings.EffectiveDownloadFolder;
            profile.PreferredTrackingPreventionLevel = Services.Settings.TrackingPrevention switch
            {
                "none" => CoreWebView2TrackingPreventionLevel.None,
                "basic" => CoreWebView2TrackingPreventionLevel.Basic,
                "strict" => CoreWebView2TrackingPreventionLevel.Strict,
                _ => CoreWebView2TrackingPreventionLevel.Balanced,
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Folder gone or setting unsupported by this runtime: keep defaults.
        }
    }

    // ───────────────────────── UI ⇄ C# bridge ─────────────────────────

    /// <summary>Sends the browser state to the UI once, after the current burst of changes.</summary>
    void QueueState()
    {
        if (_stateQueued || _closing) return;
        _stateQueued = true;
        Dispatcher.BeginInvoke(PushState, DispatcherPriority.Background);
    }

    void PushState()
    {
        _stateQueued = false;
        if (_active != null) Title = $"{DisplayTitle(_active)} — bro!{(IsPrivate ? " (private)" : "")}";
        if (!_uiReady || _closing || UiView.CoreWebView2 == null) return;

        var core = _active?.View.CoreWebView2;
        var (activeDownloads, progress) = Services.Downloads.Summary();
        var state = new
        {
            type = "state",
            tabs = _tabs.Select(t => new
            {
                id = t.Id, title = DisplayTitle(t), favicon = t.Favicon, loading = t.Loading, active = t == _active,
                audible = t.Audible, muted = t.Muted, volume = t.Volume, site = t.Site,
            }),
            groupTabs = Services.Settings.GroupTabs,
            url = _active?.Url ?? "",
            loading = _active?.Loading ?? false,
            canBack = core?.CanGoBack ?? false,
            canForward = core?.CanGoForward ?? false,
            maximized = WindowState == WindowState.Maximized,
            isPrivate = IsPrivate,
            accent = IsPrivate ? "#9b6cf0" : Services.Settings.Accent,
            searchName = Services.Settings.SearchName,
            canBookmark = CanBookmark(_active?.Url),
            bookmarked = _active != null && Services.Bookmarks.Contains(_active.Url),
            shield = ShieldState(_active),
            blocked = _active?.Blocked ?? 0,
            shieldSite = _active == null ? "" : AdBlock.FilterEngine.SiteOf(_active.PageHost),
            downloads = new { active = activeDownloads, progress, any = Services.Downloads.Items.Count > 0, flash = _downloadFlash },
        };
        _downloadFlash = false;
        UiView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(state, JsonOpts));
    }

    void FocusAddressBar()
    {
        if (!_uiReady || _closing) return;
        UiView.Focus();
        UiView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"focusAddress\"}");
    }

    void OnUiMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsUiUrl(e.Source)) return;

        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        if (msg.ValueKind != JsonValueKind.Object) return;
        string Str(string name) =>
            msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        double Num(string name) =>
            msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        var core = _active?.View.CoreWebView2;
        switch (Str("type"))
        {
            case "ready":
                _uiReady = true;
                PushState();
                if (IsNewTab(_active?.Url)) FocusAddressBar();
                break;
            case "viewport":
                _viewport = new Rect(Num("x"), Num("y"), Math.Max(0, Num("w")), Math.Max(0, Num("h")));
                LayoutTabHost();
                break;
            case "navigate": NavigateActive(Str("text")); break;
            case "back": if (core?.CanGoBack == true) core.GoBack(); break;
            case "forward": if (core?.CanGoForward == true) core.GoForward(); break;
            case "reload": core?.Reload(); break;
            case "stop": core?.Stop(); break;
            case "newTab": _ = NewTabAsync(); break;
            case "closeTab": if (FindTab((int)Num("id")) is { } toClose) CloseTab(toClose); break;
            case "activate": if (FindTab((int)Num("id")) is { } toShow) Activate(toShow); break;
            case "minimize": WindowState = WindowState.Minimized; break;
            case "toggleMax": ToggleMaximize(); break;
            case "close": Close(); break;
            case "drag": CloseSuggestions(); Native.BeginWindowDrag(Hwnd); break;
            case "menu": ShowMenu(Num("bottom"), Num("right")); break;
            case "bookmark": ToggleBookmark(); break;
            case "shield": ToggleShield(); break;
            case "downloads": OpenPage("downloads"); break;
            case "newWindow": OpenWindow(msg.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True); break;
            case "suggest": ShowSuggestions(Str("text"), Num("x"), Num("y"), Num("w")); break;
            case "suggestMove": MoveSuggestion((int)Num("delta")); break;
            case "suggestClose": CloseSuggestions(); break;
            case "overlays": SetOverlays(msg.TryGetProperty("rects", out var rects) ? rects : default); break;

            // Sound
            case "mute": if (FindTab((int)Num("id")) is { } toMute) SetMuted(toMute, Flag("muted")); break;
            case "volume": if (FindTab((int)Num("id")) is { } toLevel) SetVolume(toLevel, Num("level")); break;

            // Tab order
            case "moveTab": MoveTab((int)Num("id"), (int)Num("index")); break;
            case "detachTab": DetachTab((int)Num("id")); break;

            // Ad blocker dropdown
            case "adblockPanel": SendAdBlockPanel(); break;
            case "adblockEnable":
                Services.Settings.AdBlock = true;
                Services.SaveSettings();
                _active?.View.CoreWebView2?.Reload();
                break;
            case "openSettings": OpenPage("settings"); break;

            // Bookmarks drawer
            case "getBookmarks": SendBookmarksToUi(); break;
            case "openUrl": OpenUrl(Str("url"), Flag("newTab")); break;
            case "addBookmark":
                if (_active != null && CanBookmark(_active.Url))
                {
                    if (_active.Favicon.Length > 0) Services.Favicons.Set(_active.Url, _active.Favicon);
                    Services.Bookmarks.AddTo(_active.Url, DisplayTitle(_active), Str("folder"));
                }
                break;
            case "removeBookmark": Services.Bookmarks.Remove(Str("id")); break;
            case "updateBookmark": Services.Bookmarks.Update(Str("id"), Str("title"), Str("url"), Str("folder")); break;
            case "moveBookmark": Services.Bookmarks.Move(Str("id"), Str("beforeId"), Str("folder")); break;
            case "createFolder": Services.Bookmarks.CreateFolder(Str("name")); break;
            case "renameFolder": Services.Bookmarks.RenameFolder(Str("name"), Str("newName")); break;
            case "deleteFolder": Services.Bookmarks.DeleteFolder(Str("name")); break;
        }

        bool Flag(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    void OpenUrl(string url, bool newTab)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "file")) return;
        if (newTab || _active?.View.CoreWebView2 is not { } core) _ = CreateTabAsync(u.ToString(), activate: !newTab);
        else { core.Navigate(u.ToString()); _active.View.Focus(); }
    }

    void SendBookmarksToUi()
    {
        if (!_uiReady || UiView.CoreWebView2 == null) return;
        var payload = new
        {
            type = "bookmarks",
            items = Services.Bookmarks.ToJson(Services.Favicons),
            folders = Services.Bookmarks.Folders,
            current = _active != null && CanBookmark(_active.Url) ? _active.Url : "",
        };
        UiView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    // ───────────────────────── shared data changed ─────────────────────────

    void OnBookmarksChanged()
    {
        QueueState();
        SendBookmarksToUi();
        BroadcastToPages("bookmarks");
    }

    void OnHistoryChanged() => BroadcastToPages("historyChanged");

    void OnDownloadsChanged()
    {
        QueueState();
        BroadcastToPages("downloads");
    }

    void OnAdBlockChanged()
    {
        foreach (var t in _tabs) ApplyAdBlock(t);
        QueueState();
        BroadcastToPages("adblock");
    }

    void OnSettingsChanged()
    {
        if (UiView.CoreWebView2 != null) ApplyProfileSettings(UiView.CoreWebView2.Profile);
        if (IsPrivate && _tabs.FirstOrDefault(t => t.View.CoreWebView2 != null) is { } t0)
            ApplyProfileSettings(t0.View.CoreWebView2.Profile);
        foreach (var t in _tabs) ApplyAdBlock(t);
        QueueState();
        BroadcastToPages("settings");
        BroadcastToPages("adblock");
    }

    // ───────────────────────── layout ─────────────────────────

    /// <summary>Puts the tab views exactly over the #viewport element of the HTML UI (or the whole window in full screen).</summary>
    void LayoutTabHost()
    {
        if (_fullscreen)
        {
            TabHost.Margin = new Thickness(0);
            TabHost.Width = Root.ActualWidth;
            TabHost.Height = Root.ActualHeight;
            return;
        }
        if (_viewport.IsEmpty) return;
        var m = UiView.Margin;
        TabHost.Margin = new Thickness(m.Left + _viewport.X, m.Top + _viewport.Y, 0, 0);
        TabHost.Width = _viewport.Width;
        TabHost.Height = _viewport.Height;
    }

    /// <summary>
    /// Rounds the page's corners (a native child window can't be clipped by WPF), and cuts holes where
    /// the UI has a dropdown open, so HTML dropdowns can appear "over" the page.
    /// </summary>
    void ClipToRoundedCorners(WebView2 view)
    {
        if (PresentationSource.FromVisual(view)?.CompositionTarget is not { } target) return;
        var m = target.TransformToDevice;
        var holes = _fullscreen ? null : _overlays.Select(r => (
            (int)Math.Floor((r.X - _viewport.X) * m.M11), (int)Math.Floor((r.Y - _viewport.Y) * m.M22),
            (int)Math.Ceiling(r.Width * m.M11) + 1, (int)Math.Ceiling(r.Height * m.M22) + 1));
        Native.ClipRounded(view.Handle,
                           (int)Math.Round(view.ActualWidth * m.M11),
                           (int)Math.Round(view.ActualHeight * m.M22),
                           _fullscreen ? 0 : (int)Math.Round(PageCornerRadius * 2 * m.M11),
                           holes, (int)Math.Round(28 * m.M11)); // dropdowns have 14px corners
    }

    /// <summary>Dropdowns currently open in the UI (UI coordinates).</summary>
    List<Rect> _overlays = new();

    void SetOverlays(JsonElement rects)
    {
        var list = new List<Rect>();
        if (rects.ValueKind == JsonValueKind.Array)
            foreach (var r in rects.EnumerateArray())
                if (r.ValueKind == JsonValueKind.Object)
                    list.Add(new Rect(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
                                      Math.Max(0, r.GetProperty("w").GetDouble()), Math.Max(0, r.GetProperty("h").GetDouble())));
        _overlays = list;
        if (_active != null) ClipToRoundedCorners(_active.View);
    }

    void ClosePopovers()
    {
        if (_uiReady && !_closing) UiView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"closePopovers\"}");
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_GETMINMAXINFO) Native.FitMaximized(hwnd, lParam, coverTaskbar: _fullscreen);
        return 0;
    }

    void ApplyWindowStateLayout()
    {
        // Maximized windows can't be resized from the edges, so the UI can use the full window.
        UiView.Margin = new Thickness(WindowState == WindowState.Maximized ? 0 : ResizeStrip);
        LayoutTabHost();
    }

    void ToggleMaximize()
    {
        if (_fullscreen) { SetFullscreen(false); return; }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Full-screen video etc.: the page covers the whole monitor, taskbar included.</summary>
    void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        if (on) _stateBeforeFullscreen = WindowState;
        _fullscreen = on;
        CloseSuggestions();
        MenuPopup.IsOpen = false;
        WindowState = WindowState.Normal; // re-asks Windows for the maximized size
        WindowState = on ? WindowState.Maximized : _stateBeforeFullscreen;
        ApplyWindowStateLayout();
        if (_active != null) ClipToRoundedCorners(_active.View);
        if (!on && _active?.View.CoreWebView2 is { ContainsFullScreenElement: true } core)
            _ = core.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
    }

    // ───────────────────────── "..." menu ─────────────────────────

    void BuildMenu()
    {
        AddMenuItem("New tab", "Ctrl+T", () => _ = NewTabAsync());
        AddMenuItem("New window", "Ctrl+N", () => OpenWindow(false));
        AddMenuItem("New private window", "Ctrl+Shift+N", () => OpenWindow(true));
        AddMenuSeparator();
        AddMenuItem("History", "Ctrl+H", () => OpenPage("history"));
        AddMenuItem("Downloads", "Ctrl+J", () => OpenPage("downloads"));
        AddMenuItem("Bookmarks", "Ctrl+Shift+O", () => OpenPage("bookmarks"));
        AddMenuItem("Bookmark this page", "Ctrl+D", ToggleBookmark);
        AddMenuSeparator();
        AddMenuItem("Settings", "", () => OpenPage("settings"));
        AddMenuItem("Developer tools", "F12", () => _active?.View.CoreWebView2?.OpenDevToolsWindow());
        AddMenuSeparator();
        AddMenuItem("Close window", "", Close);
        AddMenuItem("Quit bro!", "", QuitAll);
    }

    void AddMenuSeparator() => MenuItems.Children.Add(new Border
    {
        Height = 1, Margin = new Thickness(8, 5, 8, 5),
        Background = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x3D)),
    });

    void AddMenuItem(string label, string shortcut, Action action)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label });
        var keys = new TextBlock
        {
            Text = shortcut, Margin = new Thickness(28, 0, 0, 0),
            Foreground = (Brush)FindResource("MenuMuted"),
        };
        Grid.SetColumn(keys, 1);
        row.Children.Add(keys);

        var button = new Button { Content = row, Style = (Style)FindResource("MenuButton") };
        button.Click += (_, _) => { MenuPopup.IsOpen = false; action(); };
        MenuItems.Children.Add(button);
    }

    /// <summary>Opens the menu below the "..." button, right-aligned to it (UI coordinates).</summary>
    void ShowMenu(double buttonBottom, double buttonRight)
    {
        CloseSuggestions();
        MenuCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = MenuCard.DesiredSize;
        MenuPopup.HorizontalOffset = buttonRight - size.Width;
        MenuPopup.VerticalOffset = buttonBottom + 10;
        MenuPopup.IsOpen = true;
    }

    // ───────────────────────── keyboard ─────────────────────────

    /// <summary>Shortcuts work everywhere: WebView2 forwards accelerator keys from pages to WPF.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Native.IsKeyDown(Native.VK_CONTROL);
        bool shift = Native.IsKeyDown(Native.VK_SHIFT);
        bool alt = Native.IsKeyDown(Native.VK_MENU);
        var core = _active?.View.CoreWebView2;
        bool handled = true;

        if (ctrl && e.IsRepeat && key is Key.T or Key.W or Key.D or Key.N) { }
        else if (ctrl && shift && key == Key.T) ReopenClosedTab();
        else if (ctrl && shift && key == Key.N) OpenWindow(true);
        else if (ctrl && shift && key == Key.O) OpenPage("bookmarks");
        else if (ctrl && key == Key.T) _ = NewTabAsync();
        else if (ctrl && key == Key.N) OpenWindow(false);
        else if (ctrl && key == Key.W) { if (_active != null) CloseTab(_active); }
        else if (ctrl && key == Key.D) ToggleBookmark();
        else if (ctrl && key == Key.H) OpenPage("history");
        else if (ctrl && key == Key.J) OpenPage("downloads");
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) FocusAddressBar();
        else if (ctrl && key == Key.Tab) CycleTab(shift ? -1 : 1);
        else if (ctrl && key is >= Key.D1 and <= Key.D9 && _tabs.Count > 0)
            Activate(key == Key.D9 ? _tabs[^1] : _tabs[Math.Min(key - Key.D1, _tabs.Count - 1)]);
        else if (key == Key.F5 || (ctrl && key == Key.R)) core?.Reload();
        else if (alt && key == Key.Left) { if (core?.CanGoBack == true) core.GoBack(); }
        else if (alt && key == Key.Right) { if (core?.CanGoForward == true) core.GoForward(); }
        else if (key == Key.F12) core?.OpenDevToolsWindow();
        else if (key == Key.F11) SetFullscreen(!_fullscreen);
        else if (key == Key.Escape && _fullscreen) SetFullscreen(false);
        else handled = false;

        e.Handled = handled;
        if (!handled) base.OnPreviewKeyDown(e);
    }
}
