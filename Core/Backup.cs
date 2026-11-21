using System.IO;
using System.Text.Json;

namespace Bro.Core;

/// <summary>
/// One file with your bookmarks, history and settings. Export it on one PC and import it on another
/// to keep them in sync by hand. Importing merges: nothing already in Bro is deleted.
/// </summary>
static class Backup
{
    sealed class BackupFile
    {
        public string App { get; set; } = "Bro";
        public int Version { get; set; } = 1;
        public long Exported { get; set; }
        public List<Bookmark> Bookmarks { get; set; } = new();
        public List<Visit> History { get; set; } = new();
        public Settings? Settings { get; set; }
    }

    public static string SuggestedName => $"bro! backup {DateTime.Now:yyyy-MM-dd}.json";

    public static void Export(string path)
    {
        var file = new BackupFile
        {
            Exported = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Bookmarks = Services.Bookmarks.Items.ToList(),
            History = Services.History.Visits.ToList(),
            Settings = Services.Settings,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonFile.Options));
    }

    /// <summary>Returns a sentence describing what was added.</summary>
    public static string Import(string path)
    {
        BackupFile? file;
        try { file = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path), JsonFile.Options); }
        catch (JsonException) { file = null; }
        if (file == null || file.App != "Bro")
            throw new InvalidDataException("That file isn't a bro! backup. To bring in bookmarks from another browser, use \"Import bookmarks file\".");

        int marks = Services.Bookmarks.AddMany(file.Bookmarks.Select(b => new ImportedBookmark(b.Url, b.Title, b.Folder)));
        int visits = Services.History.Merge(file.History.Select(v => new ImportedVisit(v.Url, v.Title, v.Time)));
        if (file.Settings != null) Services.ReplaceSettings(file.Settings);
        return $"Added {Plural(marks, "bookmark")} and {Plural(visits, "history entry", "history entries")}" +
               (file.Settings != null ? ", and applied the backup's settings." : ".");
    }

    public static string Plural(int n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";
}
