using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Bro.Core;

sealed record ImportedBookmark(string Url, string Title, string Folder);
sealed record ImportedVisit(string Url, string Title, long Time); // unix ms

/// <summary>A browser profile found on this PC that we can import from.</summary>
sealed record BrowserProfile(string Id, string Browser, string Profile, bool IsFirefox, string Dir)
{
    public string Label => Profile is "" or "Default" ? Browser : $"{Browser} — {Profile}";
}

/// <summary>
/// Reads bookmarks and history from Chrome, Edge, Brave, Vivaldi, Opera, Chromium and Firefox,
/// and bookmark HTML files (the format every browser can export).
/// Passwords and cookies can't be imported: each browser encrypts them so only it can read them.
/// </summary>
static class BrowserImport
{
    static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(90);
    const int MaxVisits = 10_000;

    /// <summary>
    /// Chromium keeps bookmarks in "Bookmarks", and newer Chrome versions keep the ones from a signed-in
    /// Google account in "AccountBookmarks" (same format). Both are read; duplicates are skipped later.
    /// </summary>
    static readonly string[] BookmarkFiles = { "Bookmarks", "AccountBookmarks" };

    // ───────────────────────── finding browsers ─────────────────────────

    public static List<BrowserProfile> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var found = new List<BrowserProfile>();

        foreach (var (name, userData) in new[]
                 {
                     ("Chrome", Path.Combine(local, @"Google\Chrome\User Data")),
                     ("Edge", Path.Combine(local, @"Microsoft\Edge\User Data")),
                     ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")),
                     ("Vivaldi", Path.Combine(local, @"Vivaldi\User Data")),
                     ("Chromium", Path.Combine(local, @"Chromium\User Data")),
                 })
        {
            if (!Directory.Exists(userData)) continue;
            var names = ProfileNames(userData);
            foreach (var dir in Directory.GetDirectories(userData).OrderBy(d => d))
            {
                var folder = Path.GetFileName(dir);
                if (folder != "Default" && !folder.StartsWith("Profile ")) continue;
                if (!BookmarkFiles.Any(b => File.Exists(Path.Combine(dir, b))) && !File.Exists(Path.Combine(dir, "History"))) continue;
                found.Add(new($"{name}|{folder}", name, names.TryGetValue(folder, out var n) ? n : folder, false, dir));
            }
        }

        foreach (var (name, dir) in new[]
                 {
                     ("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable")),
                     ("Opera GX", Path.Combine(roaming, @"Opera Software\Opera GX Stable")),
                 })
        {
            var profileDir = Directory.Exists(Path.Combine(dir, "Default")) ? Path.Combine(dir, "Default") : dir;
            if (BookmarkFiles.Any(b => File.Exists(Path.Combine(profileDir, b))) || File.Exists(Path.Combine(profileDir, "History")))
                found.Add(new($"{name}|", name, "", false, profileDir));
        }

        var firefox = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefox))
        {
            foreach (var dir in Directory.GetDirectories(firefox).Where(d => File.Exists(Path.Combine(d, "places.sqlite")))
                                         .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "places.sqlite"))))
            {
                var folder = Path.GetFileName(dir);
                var label = folder.Contains('.') ? folder[(folder.IndexOf('.') + 1)..] : folder;
                found.Add(new($"Firefox|{folder}", "Firefox", label == "default-release" ? "" : label, true, dir));
            }
        }
        return found;
    }

    /// <summary>Chromium keeps the names you gave your profiles in "Local State".</summary>
    static Dictionary<string, string> ProfileNames(string userData)
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(userData, "Local State")));
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache))
                foreach (var entry in cache.EnumerateObject())
                    if (entry.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                        result[entry.Name] = name;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return result;
    }

    // ───────────────────────── reading ─────────────────────────

    public static (List<ImportedBookmark> Bookmarks, List<ImportedVisit> Visits) Read(BrowserProfile p, bool bookmarks, bool history)
    {
        var marks = new List<ImportedBookmark>();
        var visits = new List<ImportedVisit>();
        if (p.IsFirefox)
        {
            using var copy = TempCopy.Of(Path.Combine(p.Dir, "places.sqlite"), "-wal");
            using var db = Open(copy.Path);
            if (bookmarks) marks = FirefoxBookmarks(db, p.Browser);
            if (history) visits = FirefoxHistory(db);
        }
        else
        {
            if (bookmarks)
                foreach (var file in BookmarkFiles.Select(b => Path.Combine(p.Dir, b)).Where(File.Exists))
                    marks.AddRange(ChromiumBookmarks(ReadShared(file), p.Browser));
            if (history && File.Exists(Path.Combine(p.Dir, "History")))
            {
                using var copy = TempCopy.Of(Path.Combine(p.Dir, "History"), "-wal", "-journal");
                using var db = Open(copy.Path);
                visits = ChromiumHistory(db);
            }
        }
        return (marks, visits);
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    /// <summary>Reads a file the browser may have open.</summary>
    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static List<ImportedBookmark> ChromiumBookmarks(string json, string browser)
    {
        var result = new List<ImportedBookmark>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;

        void Walk(JsonElement node, string folder)
        {
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : "";
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (type == "url" && node.TryGetProperty("url", out var u) && u.GetString() is { } url && IsWebUrl(url))
                result.Add(new(url, name, folder));
            else if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child, type == "folder" && name.Length > 0 && !IsRoot(node) ? $"{folder} / {name}" : folder);
        }
        bool IsRoot(JsonElement node) => roots.EnumerateObject().Any(r => r.Value.ValueKind == JsonValueKind.Object &&
            r.Value.TryGetProperty("id", out var a) && node.TryGetProperty("id", out var b) && a.GetString() == b.GetString());

        foreach (var (key, label) in new[] { ("bookmark_bar", browser), ("other", $"{browser} / Other"), ("synced", $"{browser} / Mobile") })
            if (roots.TryGetProperty(key, out var root)) Walk(root, label);
        return result;
    }

    static List<ImportedVisit> ChromiumHistory(SqliteConnection db)
    {
        // Chromium time: microseconds since 1601-01-01
        const long epochDiffMs = 11_644_473_600_000;
        var since = (DateTimeOffset.UtcNow.Add(-HistoryWindow).ToUnixTimeMilliseconds() + epochDiffMs) * 1000;
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT u.url, IFNULL(u.title, ''), v.visit_time FROM visits v JOIN urls u ON u.id = v.url
            WHERE v.visit_time > $since ORDER BY v.visit_time DESC LIMIT $max
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$max", MaxVisits);
        var result = new List<ImportedVisit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var url = r.GetString(0);
            if (IsWebUrl(url)) result.Add(new(url, r.GetString(1), r.GetInt64(2) / 1000 - epochDiffMs));
        }
        return result;
    }

    static List<ImportedVisit> FirefoxHistory(SqliteConnection db)
    {
        // Firefox time: microseconds since 1970
        var since = DateTimeOffset.UtcNow.Add(-HistoryWindow).ToUnixTimeMilliseconds() * 1000;
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.url, IFNULL(p.title, ''), v.visit_date FROM moz_historyvisits v JOIN moz_places p ON p.id = v.place_id
            WHERE v.visit_date > $since ORDER BY v.visit_date DESC LIMIT $max
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$max", MaxVisits);
        var result = new List<ImportedVisit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var url = r.GetString(0);
            if (IsWebUrl(url)) result.Add(new(url, r.GetString(1), r.GetInt64(2) / 1000));
        }
        return result;
    }

    static List<ImportedBookmark> FirefoxBookmarks(SqliteConnection db, string browser)
    {
        var nodes = new Dictionary<long, (long Parent, string Title, string Guid)>();
        var links = new List<(long Parent, string Title, string Url)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT b.id, b.type, b.parent, IFNULL(b.title, ''), IFNULL(b.guid, ''), p.url
                FROM moz_bookmarks b LEFT JOIN moz_places p ON p.id = b.fk ORDER BY b.parent, b.position
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0), type = r.GetInt64(1), parent = r.GetInt64(2);
                if (type == 2) nodes[id] = (parent, r.GetString(3), r.GetString(4));
                else if (type == 1 && !r.IsDBNull(5)) links.Add((parent, r.GetString(3), r.GetString(5)));
            }
        }

        string FolderOf(long id)
        {
            var parts = new List<string>();
            for (int guard = 0; nodes.TryGetValue(id, out var n) && guard < 50; guard++, id = n.Parent)
            {
                var root = n.Guid switch
                {
                    "toolbar_____" => browser,
                    "menu________" => $"{browser} / Menu",
                    "unfiled_____" => $"{browser} / Other",
                    "mobile______" => $"{browser} / Mobile",
                    "root________" => browser,
                    _ => null,
                };
                if (root != null) { parts.Add(root); break; }
                parts.Add(n.Title);
            }
            parts.Reverse();
            return parts.Count == 0 ? browser : string.Join(" / ", parts);
        }

        return links.Where(l => IsWebUrl(l.Url)).Select(l => new ImportedBookmark(l.Url, l.Title, FolderOf(l.Parent))).ToList();
    }

    static bool IsWebUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── bookmark HTML files ─────────────────────────

    static readonly Regex HtmlToken = new(
        @"<H3([^>]*)>(.*?)</H3>|<A\s([^>]*)>(.*?)</A>|<DL>|</DL>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Href = new(@"HREF\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses the "Netscape bookmark file" HTML that Chrome, Edge, Firefox and Safari export.</summary>
    public static List<ImportedBookmark> ParseBookmarksHtml(string html, string rootFolder)
    {
        var result = new List<ImportedBookmark>();
        var stack = new Stack<string>();
        string? pending = null;
        foreach (Match m in HtmlToken.Matches(html))
        {
            var token = m.Value;
            if (m.Groups[2].Success && token.StartsWith("<H3", StringComparison.OrdinalIgnoreCase))
            {
                // The bookmarks bar folder becomes the top level
                pending = m.Groups[1].Value.Contains("PERSONAL_TOOLBAR_FOLDER", StringComparison.OrdinalIgnoreCase)
                    ? "" : WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            }
            else if (m.Groups[4].Success && token.StartsWith("<A", StringComparison.OrdinalIgnoreCase))
            {
                var href = Href.Match(m.Groups[3].Value);
                if (!href.Success) continue;
                var url = WebUtility.HtmlDecode(href.Groups[1].Value);
                if (!IsWebUrl(url)) continue;
                var path = string.Join(" / ", stack.Reverse().Where(s => s.Length > 0));
                result.Add(new(url, WebUtility.HtmlDecode(Regex.Replace(m.Groups[4].Value, "<[^>]+>", "")).Trim(),
                               path.Length > 0 ? $"{rootFolder} / {path}" : rootFolder));
            }
            else if (token.Equals("<DL>", StringComparison.OrdinalIgnoreCase)) { stack.Push(pending ?? ""); pending = null; }
            else if (token.Equals("</DL>", StringComparison.OrdinalIgnoreCase) && stack.Count > 0) stack.Pop();
        }
        return result;
    }

    /// <summary>Bookmarks as an HTML file that any browser can import.</summary>
    public static string ToBookmarksHtml(IEnumerable<Bookmark> bookmarks)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        foreach (var group in bookmarks.GroupBy(b => b.Folder))
        {
            var indent = "    ";
            if (group.Key.Length > 0)
            {
                sb.AppendLine($"    <DT><H3>{E(group.Key)}</H3>");
                sb.AppendLine("    <DL><p>");
                indent = "        ";
            }
            foreach (var b in group)
                sb.AppendLine($"{indent}<DT><A HREF=\"{E(b.Url)}\" ADD_DATE=\"{b.Added / 1000}\">{E(b.Title)}</A>");
            if (group.Key.Length > 0) sb.AppendLine("    </DL><p>");
        }
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    /// <summary>Copies a database (and its journal files) to a temp folder, since the browser may be using it.</summary>
    sealed class TempCopy : IDisposable
    {
        readonly string _dir;
        public string Path { get; }

        TempCopy(string dir, string path) { _dir = dir; Path = path; }

        public static TempCopy Of(string source, params string[] companions)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bro-import-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(source));
            CopyShared(source, target);
            foreach (var suffix in companions)
                if (File.Exists(source + suffix)) CopyShared(source + suffix, target + suffix);
            return new TempCopy(dir, target);
        }

        static void CopyShared(string from, string to)
        {
            using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = File.Create(to);
            src.CopyTo(dst);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
