namespace Bro.Core;

sealed class Bookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Folder { get; set; } = "";   // "" = no folder
    public long Added { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Bookmarks, in the order the user arranged them. Saved to bookmarks.json.</summary>
sealed class BookmarkStore(string path)
{
    readonly List<Bookmark> _items = JsonFile.Load(path, () => new List<Bookmark>());

    public event Action? Changed;

    public IReadOnlyList<Bookmark> Items => _items;

    public bool Contains(string url) => Find(url) != null;

    Bookmark? Find(string url) => _items.FirstOrDefault(b => b.Url == url);

    /// <summary>Adds the page, or removes it if it's already saved. Returns true if it's now bookmarked.</summary>
    public bool Toggle(string url, string title)
    {
        var existing = Find(url);
        if (existing != null) _items.Remove(existing);
        else _items.Add(new Bookmark { Url = url, Title = title });
        Commit();
        return existing == null;
    }

    /// <summary>Adds imported bookmarks, skipping addresses already saved. Returns how many were added.</summary>
    public int AddMany(IEnumerable<ImportedBookmark> items)
    {
        var have = new HashSet<string>(_items.Select(b => b.Url));
        int added = 0;
        foreach (var i in items)
        {
            if (!have.Add(i.Url)) continue;
            _items.Add(new Bookmark { Url = i.Url, Title = i.Title.Trim().Length > 0 ? i.Title.Trim() : i.Url, Folder = i.Folder });
            added++;
        }
        if (added > 0) Commit();
        return added;
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(b => b.Id == id) > 0) Commit();
    }

    public void RemoveUrl(string url)
    {
        if (_items.RemoveAll(b => b.Url == url) > 0) Commit();
    }

    public void Update(string id, string title, string url, string folder)
    {
        var b = _items.FirstOrDefault(x => x.Id == id);
        if (b == null) return;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "file")
            b.Url = u.ToString();
        b.Title = title.Trim().Length > 0 ? title.Trim() : b.Title;
        b.Folder = folder.Trim();
        Commit();
    }

    /// <summary>Moves a bookmark to just before another one (or to the end when beforeId is empty).</summary>
    public void Move(string id, string beforeId, string folder)
    {
        var b = _items.FirstOrDefault(x => x.Id == id);
        if (b == null || id == beforeId) return;
        _items.Remove(b);
        b.Folder = folder.Trim();
        var index = _items.FindIndex(x => x.Id == beforeId);
        _items.Insert(index < 0 ? _items.Count : index, b);
        Commit();
    }

    void Commit()
    {
        JsonFile.Save(path, _items);
        Changed?.Invoke();
    }

    public object ToJson(FaviconCache icons) => _items.Select(b => new
    {
        id = b.Id, url = b.Url, title = b.Title, folder = b.Folder, favicon = icons.Get(b.Url),
    });
}
