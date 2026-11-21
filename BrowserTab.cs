using Microsoft.Web.WebView2.Wpf;

namespace Bro;

sealed class BrowserTab(int id, WebView2 view)
{
    public int Id { get; } = id;
    public WebView2 View { get; } = view;
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Loading { get; set; }
    public string Favicon { get; set; } = ""; // data: URL or ""

    // Ad blocker, per page
    public string PageUrl { get; set; } = "";   // the address the current page was loaded from
    public string PageHost { get; set; } = "";
    public bool PageAllowed { get; set; }       // paused site or list exception: nothing blocked
    public int Blocked { get; set; }
    public string? AdScriptId { get; set; }
    public bool AdFilterOn { get; set; }
    public int AdScriptVersion { get; set; }
}
