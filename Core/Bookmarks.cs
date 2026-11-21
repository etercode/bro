namespace Bro.Core;

sealed class Bookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Folder { get; set; } = "";   // "" = no folder
    public long Added { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Bookmarks, in the order the user arranged them, saved to bookmarks.json. Folder order (and empty
/// folders) live in bookmark-folders.json; a bookmark's Folder is the folder's name.
/// </summary>
sealed class BookmarkStore(string path)
{
    readonly List<Bookmark> _items = JsonFile.Load(path, () => new List<Bookmark>());
    readonly string _foldersPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "bookmark-folders.json");
    List<string>? _folderOrder;

    public event Action? Changed;

    public IReadOnlyList<Bookmark> Items => _items;

    List<string> FolderOrder => _folderOrder ??= JsonFile.Load(_foldersPath, () => new List<string>());

    /// <summary>All folders: the user's order first, then any that only exist on bookmarks (e.g. imported).</summary>
    public List<string> Folders =>
        FolderOrder.Concat(_items.Select(b => b.Folder)).Where(f => f.Length > 0).Distinct().ToList();

    public string CreateFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0) name = "New folder";
        var unique = name;
        for (int n = 2; Folders.Contains(unique); n++) unique = $"{name} {n}";
        FolderOrder.Add(unique);
        Commit();
        return unique;
    }

    public void RenameFolder(string from, string to)
    {
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0 || from == to) return;
        foreach (var b in _items.Where(b => b.Folder == from)) b.Folder = to;
        var order = FolderOrder;
        var i = order.IndexOf(from);
        if (i >= 0) { if (order.Contains(to)) order.RemoveAt(i); else order[i] = to; }
        Commit();
    }

    /// <summary>Deletes the folder; its bookmarks move out of it (they aren't deleted).</summary>
    public void DeleteFolder(string name)
    {
        foreach (var b in _items.Where(b => b.Folder == name)) b.Folder = "";
        FolderOrder.Remove(name);
        Commit();
    }

    /// <summary>Saves the current page into a folder (or moves it there if it's already saved).</summary>
    public void AddTo(string url, string title, string folder)
    {
        var b = Find(url);
        if (b == null) _items.Add(new Bookmark { Url = url, Title = title, Folder = folder });
        else b.Folder = folder;
        Commit();
    }

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
        if (_folderOrder != null) JsonFile.Save(_foldersPath, _folderOrder.Where(f => f.Length > 0).Distinct().ToList());
        Changed?.Invoke();
    }

    public object ToJson(FaviconCache icons) => _items.Select(b => new
    {
        id = b.Id, url = b.Url, title = b.Title, folder = b.Folder, favicon = icons.Get(b.Url),
    });
}
