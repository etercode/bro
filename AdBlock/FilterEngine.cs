using System.Text;

namespace Bro.AdBlock;

[Flags]
enum ResourceType
{
    None = 0,
    Script = 1, Image = 2, Stylesheet = 4, Object = 8, Xhr = 16, Subdocument = 32,
    Ping = 64, Media = 128, Font = 256, WebSocket = 512, Other = 1024,
    AllRequests = 2047,
}

/// <summary>One network rule from an Adblock Plus–style list, e.g. <c>||ads.example.com^$third-party</c>.</summary>
sealed class NetworkFilter
{
    public string Pattern = "";      // lowercased unless MatchCase; no anchors
    public bool HostAnchor, StartAnchor, EndAnchor, MatchCase, Exception;
    public bool? ThirdParty;         // null = either
    public ResourceType Types = ResourceType.AllRequests;
    public string[]? Domains, NotDomains;

    public bool Matches(string url, string lowerUrl, int hostStart, int hostEnd, ResourceType type,
                        string pageHost, bool thirdParty)
    {
        if ((Types & type) == 0) return false;
        if (ThirdParty is { } tp && tp != thirdParty) return false;
        if (Domains != null && !Domains.Any(d => FilterEngine.IsSameOrSubdomain(pageHost, d))) return false;
        if (NotDomains != null && NotDomains.Any(d => FilterEngine.IsSameOrSubdomain(pageHost, d))) return false;

        var u = MatchCase ? url : lowerUrl;
        if (HostAnchor)
        {
            for (int i = hostStart; i < hostEnd; i++)
                if ((i == hostStart || u[i - 1] == '.') && Glob.MatchAt(u, i, Pattern, 0, EndAnchor)) return true;
            return false;
        }
        if (StartAnchor) return Glob.MatchAt(u, 0, Pattern, 0, EndAnchor);
        return Glob.Search(u, Pattern, EndAnchor);
    }
}

static class Glob
{
    /// <summary>'^' = a separator (anything but a letter, digit, _ - . %) or the end of the address.</summary>
    static bool IsSeparator(char c) => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '%');

    public static bool MatchAt(string s, int si, string p, int pi, bool endAnchor)
    {
        while (pi < p.Length)
        {
            char c = p[pi];
            if (c == '*')
            {
                while (pi < p.Length && p[pi] == '*') pi++;
                if (pi == p.Length) return true;
                for (int k = si; k <= s.Length; k++)
                    if (MatchAt(s, k, p, pi, endAnchor)) return true;
                return false;
            }
            if (c == '^')
            {
                if (si == s.Length) { pi++; continue; }
                if (!IsSeparator(s[si])) return false;
            }
            else if (si >= s.Length || s[si] != c) return false;
            si++; pi++;
        }
        return !endAnchor || si == s.Length;
    }

    /// <summary>Unanchored match: jumps between occurrences of the pattern's first literal run.</summary>
    public static bool Search(string s, string p, bool endAnchor)
    {
        int lit = 0;
        while (lit < p.Length && p[lit] is not ('*' or '^')) lit++;
        if (lit == 0)
        {
            for (int i = 0; i <= s.Length; i++) if (MatchAt(s, i, p, 0, endAnchor)) return true;
            return false;
        }
        var head = p.AsSpan(0, lit);
        int from = 0;
        while (from <= s.Length - lit)
        {
            int idx = s.AsSpan(from).IndexOf(head, StringComparison.Ordinal);
            if (idx < 0) return false;
            idx += from;
            if (MatchAt(s, idx + lit, p, lit, endAnchor)) return true;
            from = idx + 1;
        }
        return false;
    }
}

/// <summary>
/// Parsed filter lists. Network rules are bucketed by a word that must appear in the address,
/// so each request only checks a handful of rules; plain "||domain^" rules are a hash lookup.
/// </summary>
sealed class FilterEngine
{
    static readonly HashSet<string> CommonTokens = new()
    {
        "http", "https", "www", "com", "net", "org", "html", "js", "php", "css", "png", "jpg", "gif", "cdn", "static",
    };

    readonly HashSet<string> _blockedHosts = new();
    readonly Dictionary<string, List<NetworkFilter>> _block = new();
    readonly Dictionary<string, List<NetworkFilter>> _allow = new();
    readonly List<NetworkFilter> _blockAny = new(), _allowAny = new();
    readonly List<NetworkFilter> _documentAllow = new();   // @@...$document: page fully allowed
    readonly List<NetworkFilter> _hideAllow = new();       // @@...$elemhide / $generichide

    // Cosmetic (element hiding) rules
    readonly List<string> _genericHide = new();
    readonly Dictionary<string, List<string>> _specificHide = new();
    readonly Dictionary<string, HashSet<string>> _hideExceptions = new();
    readonly HashSet<string> _noGenericHosts = new();      // hosts from simple @@||host^$generichide rules
    List<string> _exceptedGeneric = new();                 // generic selectors that some site excepts

    public int NetworkRuleCount { get; private set; }
    public int CosmeticRuleCount { get; private set; }

    public static FilterEngine Parse(IEnumerable<string> lines)
    {
        var engine = new FilterEngine();
        foreach (var raw in lines) engine.AddLine(raw.Trim());
        engine.FinishCosmetics();
        return engine;
    }

    // ───────────────────────── parsing ─────────────────────────

    void AddLine(string line)
    {
        if (line.Length == 0 || line[0] == '!' || line[0] == '[') return;

        int hash = line.IndexOf('#');
        if (hash >= 0)
        {
            if (TryAddCosmetic(line)) return;
            if (line.Contains("##") || line.Contains("#@#") || line.Contains("#?#") || line.Contains("#$#")) return;
        }
        AddNetwork(line);
    }

    bool TryAddCosmetic(string line)
    {
        bool exception;
        int at = line.IndexOf("#@#", StringComparison.Ordinal);
        int len;
        if (at >= 0) { exception = true; len = 3; }
        else
        {
            at = line.IndexOf("##", StringComparison.Ordinal);
            if (at < 0) return false;
            exception = false; len = 2;
        }
        var domainsPart = line[..at];
        var selector = line[(at + len)..].Trim();
        // Skip extended syntaxes this engine doesn't run
        if (selector.Length == 0 || domainsPart.Contains('#') || selector.StartsWith('+') ||
            selector.Contains(":-abp-") || selector.Contains(":style(") || selector.Contains(":remove(") ||
            selector.Contains(":matches-") || selector.Contains(":xpath(") || selector.Contains(":upward(") ||
            selector.Contains(":has-text(") || selector.Contains(":min-text-length(") || selector.Contains(":watch-attr("))
            return true;

        var domains = domainsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(d => d.ToLowerInvariant()).ToList();
        var include = domains.Where(d => !d.StartsWith('~')).ToList();
        var exclude = domains.Where(d => d.StartsWith('~')).Select(d => d[1..]).ToList();

        CosmeticRuleCount++;
        if (exception)
        {
            foreach (var d in include.Count > 0 ? include : new List<string> { "*" })
                (_hideExceptions.TryGetValue(d, out var set) ? set : _hideExceptions[d] = new()).Add(selector);
            return true;
        }
        if (include.Count == 0)
        {
            _genericHide.Add(selector);
            foreach (var d in exclude)
                (_hideExceptions.TryGetValue(d, out var set) ? set : _hideExceptions[d] = new()).Add(selector);
            return true;
        }
        foreach (var d in include)
            (_specificHide.TryGetValue(d, out var list) ? list : _specificHide[d] = new()).Add(selector);
        // "a.com,~b.a.com##x": a rare form; treat the excluded subdomains as exceptions
        foreach (var d in exclude)
            (_hideExceptions.TryGetValue(d, out var set) ? set : _hideExceptions[d] = new()).Add(selector);
        return true;
    }

    void AddNetwork(string line)
    {
        var f = new NetworkFilter();
        if (line.StartsWith("@@")) { f.Exception = true; line = line[2..]; }
        if (line.Length > 1 && line[0] == '/' && line.EndsWith('/')) return; // regex rules: skipped

        bool document = false, elemHide = false, genericHide = false;
        ResourceType positive = 0, negative = 0;
        int dollar = line.LastIndexOf('$');
        if (dollar > 0)
        {
            foreach (var rawOpt in line[(dollar + 1)..].Split(','))
            {
                var opt = rawOpt.Trim().ToLowerInvariant();
                bool not = opt.StartsWith('~');
                var name = not ? opt[1..] : opt;
                if (name.StartsWith("domain="))
                {
                    var ds = name[7..].Split('|', StringSplitOptions.RemoveEmptyEntries);
                    var inc = ds.Where(d => !d.StartsWith('~')).ToArray();
                    var exc = ds.Where(d => d.StartsWith('~')).Select(d => d[1..]).ToArray();
                    if (inc.Length > 0) f.Domains = inc;
                    if (exc.Length > 0) f.NotDomains = exc;
                    continue;
                }
                var type = TypeFromOption(name);
                if (type != ResourceType.None) { if (not) negative |= type; else positive |= type; continue; }
                switch (name)
                {
                    case "third-party" or "3p": f.ThirdParty = !not; break;
                    case "first-party" or "1p": f.ThirdParty = not; break;
                    case "match-case": f.MatchCase = true; break;
                    case "important": break;
                    case "document" or "doc": document = true; break;
                    case "elemhide" or "ehide": elemHide = true; break;
                    case "generichide" or "ghide": genericHide = true; break;
                    default: return; // popup, csp, redirect, removeparam…: not supported → skip the rule
                }
            }
            line = line[..dollar];
        }

        if (line.StartsWith("||")) { f.HostAnchor = true; line = line[2..]; }
        else if (line.StartsWith('|')) { f.StartAnchor = true; line = line[1..]; }
        if (line.EndsWith('|')) { f.EndAnchor = true; line = line[..^1]; }
        f.Pattern = f.MatchCase ? line : line.ToLowerInvariant();
        if (f.Pattern.Length == 0 && !f.HostAnchor && f.Domains == null) return;

        if (document || elemHide || genericHide)
        {
            if (!f.Exception) { if (positive == 0 && negative == 0) return; } // "$document" block rules: we never block pages
            else
            {
                if (document) _documentAllow.Add(f);
                if (elemHide || genericHide)
                {
                    _hideAllow.Add(f);
                    if (genericHide && !elemHide && f.HostAnchor && f.Pattern.TrimEnd('^').IndexOfAny(new[] { '/', '*', '^' }) < 0)
                        _noGenericHosts.Add(f.Pattern.TrimEnd('^'));
                }
                if (positive == 0 && negative == 0) { NetworkRuleCount++; return; }
            }
        }

        if (positive != 0) f.Types = positive;
        if (negative != 0) f.Types &= ~negative;
        NetworkRuleCount++;

        // Fast path: plain "||host^" block rules
        if (!f.Exception && f.HostAnchor && f.ThirdParty == null && f.Domains == null && f.NotDomains == null &&
            f.Types == ResourceType.AllRequests && f.Pattern.EndsWith('^') && !f.MatchCase)
        {
            var host = f.Pattern[..^1];
            if (host.Length > 0 && host.IndexOfAny(new[] { '/', '*', '^', '|' }) < 0) { _blockedHosts.Add(host); return; }
        }

        var token = BestToken(f);
        var index = f.Exception ? _allow : _block;
        if (token == null) (f.Exception ? _allowAny : _blockAny).Add(f);
        else (index.TryGetValue(token, out var list) ? list : index[token] = new()).Add(f);
    }

    static ResourceType TypeFromOption(string name) => name switch
    {
        "script" => ResourceType.Script,
        "image" => ResourceType.Image,
        "stylesheet" or "css" => ResourceType.Stylesheet,
        "object" or "object-subrequest" => ResourceType.Object,
        "xmlhttprequest" or "xhr" => ResourceType.Xhr,
        "subdocument" or "frame" => ResourceType.Subdocument,
        "ping" or "beacon" => ResourceType.Ping,
        "media" => ResourceType.Media,
        "font" => ResourceType.Font,
        "websocket" => ResourceType.WebSocket,
        "other" => ResourceType.Other,
        _ => ResourceType.None,
    };

    static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '%';

    /// <summary>The longest word of the pattern that is guaranteed to be a whole word of a matching address.</summary>
    static string? BestToken(NetworkFilter f)
    {
        var p = f.Pattern;
        if (f.MatchCase) p = p.ToLowerInvariant();
        string? best = null;
        int i = 0;
        while (i < p.Length)
        {
            if (!IsTokenChar(p[i])) { i++; continue; }
            int start = i;
            while (i < p.Length && IsTokenChar(p[i])) i++;
            bool leftOk = start > 0 ? p[start - 1] != '*' : f.HostAnchor || f.StartAnchor;
            bool rightOk = i < p.Length ? p[i] != '*' : f.EndAnchor;
            if (!leftOk || !rightOk) continue;
            var token = p[start..i];
            if (CommonTokens.Contains(token) || token.Length < 2) continue;
            if (best == null || token.Length > best.Length) best = token;
        }
        return best;
    }

    void FinishCosmetics()
    {
        // Generic selectors that any site excepts can't go in the shared stylesheet; they're applied per site.
        var excepted = new HashSet<string>(_hideExceptions.Values.SelectMany(s => s));
        var global = _hideExceptions.TryGetValue("*", out var g) ? g : new HashSet<string>();
        _exceptedGeneric = _genericHide.Where(s => excepted.Contains(s) && !global.Contains(s)).Distinct().ToList();
        _genericHide.RemoveAll(s => excepted.Contains(s));
    }

    // ───────────────────────── matching ─────────────────────────

    public static bool IsSameOrSubdomain(string host, string domain) =>
        host.Length == domain.Length ? host == domain :
        host.Length > domain.Length && host.EndsWith(domain, StringComparison.Ordinal) && host[^(domain.Length + 1)] == '.';

    /// <summary>Approximate "site" of a host: example.com for a.b.example.com, example.co.uk for x.example.co.uk.</summary>
    public static string SiteOf(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        bool countrySecondLevel = parts[^1].Length == 2 &&
            parts[^2] is "co" or "com" or "org" or "net" or "gov" or "ac" or "edu" or "ne" or "or" or "go";
        int keep = countrySecondLevel ? 3 : 2;
        return string.Join('.', parts[^keep..]);
    }

    static (int Start, int End) HostRange(string url)
    {
        int s = url.IndexOf("://", StringComparison.Ordinal);
        if (s < 0) return (0, 0);
        s += 3;
        int at = url.IndexOf('@', s);
        int e = s;
        while (e < url.Length && url[e] is not ('/' or '?' or '#' or ':')) e++;
        if (at >= 0 && at < e) { s = at + 1; e = s; while (e < url.Length && url[e] is not ('/' or '?' or '#' or ':')) e++; }
        return (s, e);
    }

    public static string HostOf(string url)
    {
        var (s, e) = HostRange(url);
        return url[s..e].ToLowerInvariant();
    }

    /// <summary>Should this request (made by a page on <paramref name="pageHost"/>) be blocked?</summary>
    public bool ShouldBlock(string url, ResourceType type, string pageHost)
    {
        var lower = url.ToLowerInvariant();
        var (hs, he) = HostRange(lower);
        if (he <= hs) return false;
        var host = lower[hs..he];
        bool thirdParty = SiteOf(host) != SiteOf(pageHost);

        bool blocked = false;
        for (var d = host; ; )
        {
            if (_blockedHosts.Contains(d)) { blocked = true; break; }
            int dot = d.IndexOf('.');
            if (dot < 0) break;
            d = d[(dot + 1)..];
        }
        if (!blocked) blocked = AnyMatch(_block, _blockAny, url, lower, hs, he, type, pageHost, thirdParty);
        if (!blocked) return false;
        return !AnyMatch(_allow, _allowAny, url, lower, hs, he, type, pageHost, thirdParty);
    }

    static bool AnyMatch(Dictionary<string, List<NetworkFilter>> index, List<NetworkFilter> untokenized,
                         string url, string lower, int hs, int he, ResourceType type, string pageHost, bool thirdParty)
    {
        int i = 0;
        while (i < lower.Length)
        {
            if (!IsTokenChar(lower[i])) { i++; continue; }
            int start = i;
            while (i < lower.Length && IsTokenChar(lower[i])) i++;
            if (index.Count > 0 && index.TryGetValue(lower[start..i], out var list))
                foreach (var f in list)
                    if (f.Matches(url, lower, hs, he, type, pageHost, thirdParty)) return true;
        }
        foreach (var f in untokenized)
            if (f.Matches(url, lower, hs, he, type, pageHost, thirdParty)) return true;
        return false;
    }

    /// <summary>Page-level exceptions (<c>@@||site^$document</c>): nothing is blocked or hidden there.</summary>
    public bool IsPageAllowed(string pageUrl)
    {
        var lower = pageUrl.ToLowerInvariant();
        var (hs, he) = HostRange(lower);
        if (he <= hs) return true;
        var host = lower[hs..he];
        return _documentAllow.Any(f => f.Matches(pageUrl, lower, hs, he, ResourceType.AllRequests, host, false));
    }

    // ───────────────────────── element hiding ─────────────────────────

    /// <summary>One stylesheet for every site (only selectors no site makes an exception for).</summary>
    public string GenericCss() => ToCss(_genericHide);

    public IReadOnlyCollection<string> NoGenericHosts => _noGenericHosts;

    /// <summary>Extra rules for one site: its own hiding rules plus generic ones it doesn't except.</summary>
    public string SiteCss(string pageUrl)
    {
        var lower = pageUrl.ToLowerInvariant();
        var (hs, he) = HostRange(lower);
        if (he <= hs) return "";
        var host = lower[hs..he];
        if (_hideAllow.Any(f => f.Matches(pageUrl, lower, hs, he, ResourceType.AllRequests, host, false))) return "";

        var exceptions = new HashSet<string>();
        var selectors = new List<string>();
        for (var d = host; ; )
        {
            if (_hideExceptions.TryGetValue(d, out var ex)) exceptions.UnionWith(ex);
            if (_specificHide.TryGetValue(d, out var list)) selectors.AddRange(list);
            int dot = d.IndexOf('.');
            if (dot < 0) break;
            d = d[(dot + 1)..];
        }
        selectors.AddRange(_exceptedGeneric);
        return ToCss(selectors.Where(s => !exceptions.Contains(s)).Distinct());
    }

    /// <summary>Each selector is its own rule, so one the browser doesn't understand can't break the rest.</summary>
    static string ToCss(IEnumerable<string> selectors)
    {
        var sb = new StringBuilder();
        foreach (var s in selectors)
        {
            if (s.Contains('{') || s.Contains('}') || s.Contains("</")) continue;
            sb.Append(s).Append("{display:none!important}\n");
        }
        return sb.ToString();
    }
}
