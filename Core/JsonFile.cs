using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Bro.Core;

/// <summary>Reads and writes the small JSON files in %LOCALAPPDATA%\Bro.</summary>
static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable file: start fresh rather than refusing to open the browser.
        }
        return fallback();
    }

    /// <summary>Writes to a temp file first, so a crash mid-write never leaves a broken file.</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Couldn't write; the change still applies for this session.
        }
    }
}

/// <summary>Runs an action once, a moment after the last request (e.g. to save after a burst of changes).</summary>
sealed class Debouncer
{
    readonly DispatcherTimer _timer;
    readonly Action _action;

    public Debouncer(TimeSpan delay, Action action)
    {
        _action = action;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        _timer.Tick += (_, _) => Flush();
    }

    public bool Pending => _timer.IsEnabled;

    public void Request() { _timer.Stop(); _timer.Start(); }

    public void Flush()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _action();
    }
}
