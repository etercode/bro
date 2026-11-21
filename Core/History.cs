namespace Bro.Core;

sealed class Visit
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public long Time { get; set; } // unix ms
}

/// <summary>Pages you visited (not in private windows). Saved to history.json, newest last.</summary>
sealed class HistoryStore
{
    const int MaxVisits = 10_000;
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    readonly string _path;
    readonly List<Visit> _visits;
    readonly Debouncer _save;
    long _nextId;

    public HistoryStore(string path)
    {
        _path = path;
        var cutoff = DateTimeOffset.UtcNow.Add(-MaxAge).ToUnixTimeMilliseconds();
        _visits = JsonFile.Load(path, () => new List<Visit>()).Where(v => v.Time >= cutoff).ToList();
        _nextId = _visits.Count == 0 ? 1 : _visits.Max(v => v.Id) + 1;
        _save = new Debouncer(TimeSpan.FromSeconds(2), Save);
    }

    public event Action? Changed;

    public IReadOnlyList<Visit> Visits => _visits;

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static bool ShouldRecord(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" &&
        !u.Host.EndsWith(".bro", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records a visit. Reloads and repeated visits within a minute update the last entry instead.</summary>
    public void Add(string url, string title)
    {
        if (!ShouldRecord(url)) return;
        var last = _visits.Count > 0 ? _visits[^1] : null;
        if (last != null && last.Url == url && Now - last.Time < 60_000)
        {
            last.Time = Now;
            if (title.Length > 0) last.Title = title;
        }
        else
        {
            _visits.Add(new Visit { Id = _nextId++, Url = url, Title = title, Time = Now });
            if (_visits.Count > MaxVisits) _visits.RemoveRange(0, _visits.Count - MaxVisits);
        }
        Commit();
    }

    /// <summary>Titles often arrive after the visit was recorded.</summary>
    public void UpdateTitle(string url, string title)
    {
        if (title.Length == 0) return;
        for (int i = _visits.Count - 1; i >= Math.Max(0, _visits.Count - 20); i--)
        {
            if (_visits[i].Url != url) continue;
            if (_visits[i].Title == title) return;
            _visits[i].Title = title;
            Commit();
            return;
        }
    }

    /// <summary>Adds imported visits (from the last 90 days), skipping ones already here. Returns how many were added.</summary>
    public int Merge(IEnumerable<ImportedVisit> items)
    {
        var cutoff = DateTimeOffset.UtcNow.Add(-MaxAge).ToUnixTimeMilliseconds();
        var have = new HashSet<(string, long)>(_visits.Select(v => (v.Url, v.Time / 1000)));
        int added = 0;
        foreach (var i in items)
        {
            if (i.Time < cutoff || i.Time > Now + 60_000 || !ShouldRecord(i.Url) || !have.Add((i.Url, i.Time / 1000))) continue;
            _visits.Add(new Visit { Id = _nextId++, Url = i.Url, Title = i.Title, Time = i.Time });
            added++;
        }
        if (added == 0) return 0;
        _visits.Sort((a, b) => a.Time.CompareTo(b.Time));
        if (_visits.Count > MaxVisits) _visits.RemoveRange(0, _visits.Count - MaxVisits);
        Commit();
        return added;
    }

    public void Remove(long id)
    {
        if (_visits.RemoveAll(v => v.Id == id) > 0) Commit();
    }

    public void RemoveSince(long sinceMs)
    {
        if (_visits.RemoveAll(v => v.Time >= sinceMs) > 0) Commit();
    }

    /// <summary>Newest first, matching every word of the query against title and address.</summary>
    public IEnumerable<Visit> Search(string query, int limit)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<Visit> result = Enumerable.Reverse(_visits);
        if (words.Length > 0)
            result = result.Where(v => words.All(w =>
                v.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                v.Url.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return result.Take(limit);
    }

    void Commit()
    {
        _save.Request();
        Changed?.Invoke();
    }

    void Save() => JsonFile.Save(_path, _visits);
    public void Flush() => _save.Flush();
}
