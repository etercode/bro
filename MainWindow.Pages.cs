using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Bro.Core;

namespace Bro;

/// <summary>Talks to Bro's own pages (bro://history, bookmarks, downloads, settings, the new tab page).</summary>
public partial class MainWindow
{
    /// <summary>Only our own pages may ask for anything; messages from websites are ignored.</summary>
    void OnPageMessage(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsPagesUrl(e.Source)) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        if (msg.ValueKind != JsonValueKind.Object) return;
        string Str(string name) =>
            msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long Num(string name) =>
            msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        bool Flag(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        switch (Str("type"))
        {
            case "get":
                foreach (var what in Str("what").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    SendToPage(core, what);
                break;
            case "history":
                SendHistory(core, Str("query"), (int)Math.Clamp(Num("limit"), 1, 2000));
                break;
            case "removeVisit": Services.History.Remove(Num("id")); break;

            case "removeBookmark": Services.Bookmarks.Remove(Str("id")); break;
            case "updateBookmark": Services.Bookmarks.Update(Str("id"), Str("title"), Str("url"), Str("folder")); break;
            case "moveBookmark": Services.Bookmarks.Move(Str("id"), Str("beforeId"), Str("folder")); break;

            case "download": Services.Downloads.Act(Num("id"), Str("action")); break;
            case "clearDownloads": Services.Downloads.ClearFinished(); break;

            case "setSetting":
                if (msg.TryGetProperty("value", out var value) && Services.Settings.Set(Str("key"), value))
                    Services.SaveSettings();
                break;
            case "pickDownloadFolder": PickDownloadFolder(); break;
            case "unpauseSite":
                Services.Settings.AdBlockPausedSites.Remove(Str("site"));
                Services.SaveSettings();
                break;
            case "updateLists": _ = Services.AdBlock.UpdateAsync(); break;
            case "clearData":
                _ = ClearBrowsingDataAsync(core, Str("range"), Flag("history"), Flag("cookies"), Flag("cache"), Flag("downloads"));
                break;
            case "getBrowsers": _ = SendBrowsersAsync(core); break;
            case "importBrowser": _ = ImportFromBrowserAsync(core, Str("id"), Flag("bookmarks"), Flag("history")); break;
            case "importHtml": ImportBookmarksFile(core); break;
            case "exportHtml": ExportBookmarksFile(core); break;
            case "exportData": ExportBackup(core); break;
            case "importData": ImportBackup(core); break;
            case "openDataFolder":
                Directory.CreateDirectory(Services.DataDir);
                Process.Start("explorer.exe", $"\"{Services.DataDir}\"");
                break;
        }
    }

    void SendToPage(CoreWebView2 core, string what)
    {
        object? payload = what switch
        {
            "settings" => new
            {
                type = "settings",
                settings = Services.Settings.ToJson(),
                isPrivate = IsPrivate,
                accent = IsPrivate ? "#9b6cf0" : Services.Settings.Accent,
                version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "",
                dataDir = Services.DataDir,
            },
            "bookmarks" => new { type = "bookmarks", items = Services.Bookmarks.ToJson(Services.Favicons) },
            "downloads" => new { type = "downloads", items = Services.Downloads.ToJson() },
            "adblock" => new
            {
                type = "adblock",
                enabled = Services.Settings.AdBlock,
                status = Services.AdBlock.Status,
                updating = Services.AdBlock.Updating,
                updated = Services.AdBlock.ListsUpdated?.ToString("d MMM yyyy, HH:mm") ?? "",
                lists = AdBlock.AdBlocker.Lists.Select(l => l.Name),
                paused = Services.Settings.AdBlockPausedSites,
            },
            "historyChanged" => new { type = "historyChanged" },
            _ => null,
        };
        if (payload == null) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts)); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    void SendHistory(CoreWebView2 core, string query, int limit)
    {
        var items = Services.History.Search(query, limit).Select(v => new
        {
            id = v.Id, url = v.Url, title = v.Title, time = v.Time, favicon = Services.Favicons.Get(v.Url),
        });
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "history", query, items }, JsonOpts));
    }

    /// <summary>Sends fresh data to every open Bro page in this window.</summary>
    void BroadcastToPages(string what)
    {
        foreach (var t in _tabs)
            if (t.View.CoreWebView2 is { } core && IsPagesUrl(core.Source))
                SendToPage(core, what);
    }

    // ───────────────────────── import / export ─────────────────────────

    List<BrowserProfile> _browsers = new();

    async Task SendBrowsersAsync(CoreWebView2 core)
    {
        _browsers = await Task.Run(BrowserImport.Detect);
        Post(core, new { type = "browsers", items = _browsers.Select(b => new { id = b.Id, label = b.Label }) });
    }

    async Task ImportFromBrowserAsync(CoreWebView2 core, string id, bool bookmarks, bool history)
    {
        var profile = _browsers.FirstOrDefault(b => b.Id == id);
        if (profile == null || (!bookmarks && !history)) return;
        try
        {
            var (marks, visits) = await Task.Run(() => BrowserImport.Read(profile, bookmarks, history));
            int addedMarks = Services.Bookmarks.AddMany(marks);
            int addedVisits = Services.History.Merge(visits);
            var parts = new List<string>();
            if (bookmarks) parts.Add(Backup.Plural(addedMarks, "bookmark"));
            if (history) parts.Add(Backup.Plural(addedVisits, "history entry", "history entries"));
            var skipped = bookmarks && marks.Count > addedMarks ? $" ({marks.Count - addedMarks:N0} bookmarks were already in bro!)" : "";
            ImportResult(core, true, $"Imported {string.Join(" and ", parts)} from {profile.Label}{skipped}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or JsonException)
        {
            ImportResult(core, false, $"Couldn't read {profile.Browser}'s data. Close {profile.Browser} and try again. ({e.Message})");
        }
    }

    void ImportBookmarksFile(CoreWebView2 core)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import bookmarks", Filter = "Bookmark files (*.html)|*.html;*.htm|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var found = BrowserImport.ParseBookmarksHtml(File.ReadAllText(dialog.FileName), "Imported");
            if (found.Count == 0) { ImportResult(core, false, "No bookmarks found in that file."); return; }
            int added = Services.Bookmarks.AddMany(found);
            ImportResult(core, true, $"Imported {Backup.Plural(added, "bookmark")} into the \"Imported\" folder" +
                                     (found.Count > added ? $" ({found.Count - added:N0} were already in bro!)." : "."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ImportResult(core, false, "Couldn't read that file: " + e.Message);
        }
    }

    void ExportBookmarksFile(CoreWebView2 core)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export bookmarks", FileName = "bro! bookmarks.html", Filter = "Bookmark file (*.html)|*.html",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, BrowserImport.ToBookmarksHtml(Services.Bookmarks.Items));
            ImportResult(core, true, $"Saved {Backup.Plural(Services.Bookmarks.Items.Count, "bookmark")} to {Path.GetFileName(dialog.FileName)}. Any browser can import this file.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ImportResult(core, false, "Couldn't save: " + e.Message);
        }
    }

    void ExportBackup(CoreWebView2 core)
    {
        Services.Flush();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export bro! data", FileName = Backup.SuggestedName, Filter = "bro! backup (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Backup.Export(dialog.FileName);
            ImportResult(core, true, $"Saved {Path.GetFileName(dialog.FileName)}. Import it in bro! on another PC to bring everything over.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ImportResult(core, false, "Couldn't save: " + e.Message);
        }
    }

    void ImportBackup(CoreWebView2 core)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import bro! data", Filter = "bro! backup (*.json)|*.json|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportResult(core, true, Backup.Import(dialog.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ImportResult(core, false, e.Message);
        }
    }

    void ImportResult(CoreWebView2 core, bool ok, string text) => Post(core, new { type = "importResult", ok, text });

    static void Post(CoreWebView2 core, object payload)
    {
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts)); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    void PickDownloadFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where downloads are saved",
            InitialDirectory = Services.Settings.EffectiveDownloadFolder,
        };
        if (dialog.ShowDialog(this) != true) return;
        Services.Settings.DownloadFolder = dialog.FolderName;
        Services.SaveSettings();
    }

    async Task ClearBrowsingDataAsync(CoreWebView2 core, string range, bool history, bool cookies, bool cache, bool downloads)
    {
        var since = range switch
        {
            "hour" => DateTime.Now.AddHours(-1),
            "day" => DateTime.Now.AddDays(-1),
            "week" => DateTime.Now.AddDays(-7),
            _ => DateTime.MinValue,
        };
        if (history)
        {
            Services.History.RemoveSince(since == DateTime.MinValue ? 0 : new DateTimeOffset(since).ToUnixTimeMilliseconds());
            if (range == "all") Services.Favicons.Clear();
        }
        if (downloads) Services.Downloads.ClearFinished();

        var kinds = (CoreWebView2BrowsingDataKinds)0;
        if (cookies) kinds |= CoreWebView2BrowsingDataKinds.AllSite;
        if (cache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
        if (kinds != 0 && UiView.CoreWebView2?.Profile is { } profile)
        {
            try
            {
                if (range == "all") await profile.ClearBrowsingDataAsync(kinds);
                else await profile.ClearBrowsingDataAsync(kinds, since, DateTime.Now);
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        try { core.PostWebMessageAsJson("{\"type\":\"cleared\"}"); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
}
