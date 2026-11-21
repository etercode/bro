using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Bro.Core;

sealed class DownloadItem
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public long Received { get; set; }
    public long Total { get; set; }          // -1 when the server didn't say
    public string State { get; set; } = "";  // inProgress | paused | completed | failed | cancelled
    public long Started { get; set; }
    public bool Private { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public CoreWebView2DownloadOperation? Operation { get; set; }
}

/// <summary>Tracks downloads from every window. Finished ones are remembered in downloads.json.</summary>
sealed class DownloadManager
{
    const int MaxRemembered = 200;

    readonly string _path;
    readonly List<DownloadItem> _items;
    readonly Debouncer _notify;
    long _nextId;

    public DownloadManager(string path)
    {
        _path = path;
        _items = JsonFile.Load(path, () => new List<DownloadItem>());
        foreach (var d in _items.Where(d => d.State is "inProgress" or "paused")) d.State = "failed";
        _nextId = _items.Count == 0 ? 1 : _items.Max(d => d.Id) + 1;
        _notify = new Debouncer(TimeSpan.FromMilliseconds(250), () => Changed?.Invoke());
    }

    /// <summary>Raised (at most a few times a second) whenever progress or the list changes.</summary>
    public event Action? Changed;
    /// <summary>Raised once when a download starts, so the window can draw attention to it.</summary>
    public event Action<DownloadItem>? Started;

    public IReadOnlyList<DownloadItem> Items => _items;

    public void Track(CoreWebView2DownloadOperation op, bool isPrivate)
    {
        var item = new DownloadItem
        {
            Id = _nextId++, Url = op.Uri, Path = op.ResultFilePath,
            Total = op.TotalBytesToReceive is { } t ? (long)t : -1,
            State = "inProgress", Started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Private = isPrivate, Operation = op,
        };
        _items.Insert(0, item);
        op.BytesReceivedChanged += (_, _) => { item.Received = op.BytesReceived; _notify.Request(); };
        op.EstimatedEndTimeChanged += (_, _) => _notify.Request();
        op.StateChanged += (_, _) => OnStateChanged(item, op);
        Started?.Invoke(item);
        Commit();
    }

    void OnStateChanged(DownloadItem item, CoreWebView2DownloadOperation op)
    {
        item.Path = op.ResultFilePath;
        item.Received = op.BytesReceived;
        if (op.TotalBytesToReceive is { } t) item.Total = (long)t;
        item.State = op.State switch
        {
            CoreWebView2DownloadState.Completed => "completed",
            CoreWebView2DownloadState.Interrupted => op.InterruptReason switch
            {
                CoreWebView2DownloadInterruptReason.UserPaused => "paused",
                CoreWebView2DownloadInterruptReason.UserCanceled => "cancelled",
                _ => "failed",
            },
            _ => "inProgress",
        };
        if (item.State is "completed" or "cancelled") item.Operation = null;
        Commit();
    }

    public void Act(long id, string action)
    {
        var item = _items.FirstOrDefault(d => d.Id == id);
        if (item == null) return;
        var op = item.Operation;
        try
        {
            switch (action)
            {
                case "pause": op?.Pause(); break;
                case "resume": if (op?.CanResume == true) op.Resume(); break;
                case "cancel": op?.Cancel(); break;
                case "open":
                    if (File.Exists(item.Path)) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                    break;
                case "show":
                    if (File.Exists(item.Path)) Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
                    else if (Directory.Exists(System.IO.Path.GetDirectoryName(item.Path)))
                        Process.Start("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(item.Path)}\"");
                    break;
                case "remove":
                    if (item.State is "inProgress" or "paused") op?.Cancel();
                    _items.Remove(item);
                    Commit();
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // The download or file went away meanwhile.
        }
    }

    public void ClearFinished()
    {
        _items.RemoveAll(d => d.State is not ("inProgress" or "paused"));
        Commit();
    }

    /// <summary>For the toolbar: how many are running and overall progress (or -1 if unknown).</summary>
    public (int Active, double Progress) Summary()
    {
        var active = _items.Where(d => d.State == "inProgress").ToList();
        if (active.Count == 0) return (0, 0);
        if (active.Any(d => d.Total <= 0)) return (active.Count, -1);
        return (active.Count, (double)active.Sum(d => d.Received) / active.Sum(d => d.Total));
    }

    void Commit()
    {
        var keep = _items.Where(d => !d.Private && d.State is "completed" or "failed" or "cancelled")
                         .Take(MaxRemembered).ToList();
        JsonFile.Save(_path, keep);
        _notify.Request();
    }

    public object ToJson() => _items.Select(d => new
    {
        id = d.Id, url = d.Url, path = d.Path, name = System.IO.Path.GetFileName(d.Path),
        received = d.Received, total = d.Total, state = d.State, started = d.Started,
        exists = d.State == "completed" && File.Exists(d.Path),
        canResume = d.Operation?.CanResume == true,
    });
}
