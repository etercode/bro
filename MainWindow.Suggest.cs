using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bro.Core;

namespace Bro;

/// <summary>
/// Address bar suggestions. They're a native popup (not HTML) because the page, a separate
/// window, would otherwise cover anything the UI drew below the address bar.
/// </summary>
public partial class MainWindow
{
    sealed record Suggestion(string Kind, string Title, string Url, string Favicon); // kind: search | url | bookmark | history

    readonly List<Suggestion> _suggestions = new();
    int _suggestIndex;
    string _suggestText = "";

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3));
    static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x95));
    static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x39));

    void ShowSuggestions(string text, double x, double y, double width)
    {
        _suggestText = text;
        _suggestions.Clear();
        _suggestIndex = 0;
        if (text.Trim().Length == 0 || _active == null) { CloseSuggestions(); return; }

        // First row: what Enter does
        var target = ResolveInput(text);
        if (target.StartsWith(Services.Settings.SearchUrl, StringComparison.Ordinal))
            _suggestions.Add(new("search", text.Trim(), $"Search {Services.Settings.SearchName}", ""));
        else
            _suggestions.Add(new("url", text.Trim(), target, Services.Favicons.Get(target)));

        _suggestions.AddRange(FindMatches(text.Trim(), 6)
            .Where(s => !string.Equals(s.Url.TrimEnd('/'), target.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));

        RenderSuggestions();
        SuggestCard.Width = Math.Max(280, width);
        SuggestPopup.HorizontalOffset = x;
        SuggestPopup.VerticalOffset = y + 8;
        SuggestPopup.IsOpen = true;
    }

    /// <summary>Bookmarks and history ranked by how well and how often they match.</summary>
    static IEnumerable<Suggestion> FindMatches(string text, int limit)
    {
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scores = new Dictionary<string, (double Score, Suggestion S)>();

        static string Bare(string url)
        {
            var s = url.ToLowerInvariant();
            foreach (var p in new[] { "https://", "http://" }) if (s.StartsWith(p)) s = s[p.Length..];
            return s.StartsWith("www.") ? s[4..] : s;
        }

        void Consider(string url, string title, string kind, double bonus)
        {
            var bare = Bare(url);
            var lowerTitle = title.ToLowerInvariant();
            if (!words.All(w => bare.Contains(w) || lowerTitle.Contains(w))) return;
            double score = bonus;
            if (bare.StartsWith(words[0])) score += 100;
            else if (bare.Contains("." + words[0]) || bare.Contains("/" + words[0])) score += 40;
            if (lowerTitle.StartsWith(words[0])) score += 30;
            score -= bare.Length / 20.0; // prefer short addresses (home pages)
            if (scores.TryGetValue(url, out var existing))
            {
                var best = kind == "bookmark" ? new Suggestion(kind, title, url, existing.S.Favicon) : existing.S;
                scores[url] = (Math.Max(existing.Score, score) + 3, best); // repeated visits rank higher
            }
            else scores[url] = (score, new Suggestion(kind, title.Length > 0 ? title : bare, url, Services.Favicons.Get(url)));
        }

        foreach (var b in Services.Bookmarks.Items) Consider(b.Url, b.Title, "bookmark", 25);
        var visits = Services.History.Visits;
        for (int i = visits.Count - 1, n = 0; i >= 0 && n < 3000; i--, n++)
            Consider(visits[i].Url, visits[i].Title, "history", 0);

        return scores.Values.OrderByDescending(v => v.Score).Take(limit).Select(v => v.S);
    }

    void RenderSuggestions()
    {
        SuggestItems.Children.Clear();
        for (int i = 0; i < _suggestions.Count; i++)
        {
            var s = _suggestions[i];
            int index = i;
            var row = new Grid { Height = 44 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(IconFor(s));

            var texts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = s.Title, Foreground = TextBrush, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
            });
            texts.Children.Add(new TextBlock
            {
                Text = s.Kind == "search" ? "  —  " + s.Url : "  —  " + DisplayUrl(s.Url),
                Foreground = MutedBrush, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);

            var item = new Border
            {
                Child = row, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = index == _suggestIndex ? SelectedBrush : Brushes.Transparent,
                ClipToBounds = true,
            };
            item.MouseEnter += (_, _) => { if (_suggestIndex != index) { _suggestIndex = index; HighlightSuggestion(); } };
            item.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; PickSuggestion(index); };
            SuggestItems.Children.Add(item);
        }
    }

    static FrameworkElement IconFor(Suggestion s)
    {
        if (s.Favicon.StartsWith("data:image/png;base64,") && s.Kind != "search")
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(s.Favicon[22..]));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                return new Image { Source = bmp, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center };
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or System.IO.IOException) { }
        }
        var glyph = s.Kind switch { "search" => "", "bookmark" => "", "history" => "", _ => "" };
        return new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = 16, Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static string DisplayUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var host = u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;
        var rest = u.PathAndQuery == "/" ? "" : u.PathAndQuery;
        return host + rest;
    }

    void HighlightSuggestion()
    {
        for (int i = 0; i < SuggestItems.Children.Count; i++)
            ((Border)SuggestItems.Children[i]).Background = i == _suggestIndex ? SelectedBrush : Brushes.Transparent;
    }

    /// <summary>Arrow keys: highlight another row and put its address in the address bar.</summary>
    void MoveSuggestion(int delta)
    {
        if (!SuggestPopup.IsOpen || _suggestions.Count == 0) return;
        _suggestIndex = Math.Clamp(_suggestIndex + delta, 0, _suggestions.Count - 1);
        HighlightSuggestion();
        var s = _suggestions[_suggestIndex];
        var fill = s.Kind is "search" or "url" ? _suggestText : s.Url;
        UiView.CoreWebView2?.PostWebMessageAsJson(JsonSerializerFill(fill));
    }

    static string JsonSerializerFill(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "fill", text });

    void PickSuggestion(int index)
    {
        if (index < 0 || index >= _suggestions.Count) return;
        var s = _suggestions[index];
        NavigateActive(s.Kind is "search" or "url" ? _suggestText : s.Url);
        UiView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"blurAddress\"}");
    }

    void CloseSuggestions()
    {
        if (SuggestPopup.IsOpen) SuggestPopup.IsOpen = false;
    }
}
