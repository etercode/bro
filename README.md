# bro!

A small Chromium-based browser for Windows: C# / WPF + WebView2, with the whole interface written in HTML/CSS.

## Run

```powershell
dotnet run
```

## Make the installer

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

This creates `dist\bro-setup.exe` (about 60 MB, includes .NET, so the target PC needs nothing else).
It installs per user to `%LOCALAPPDATA%\Programs\Bro` (no admin rights), adds Start menu and desktop
shortcuts, and lists Bro under Settings › Apps, where **Uninstall** runs `Bro.exe --uninstall`.

## Logo

`logo.png` (project root) is the source. After changing it, run
`powershell -ExecutionPolicy Bypass -File assets\make-icons.ps1` to regenerate the app icon
(`assets\bro.ico`), the installer image and the logos in `ui\` and `pages\`.

## Where things are

| File | What it does |
|---|---|
| `ui/index.html`, `ui/style.css`, `ui/app.js` | The browser interface: toolbar, tabs, address bar. **Design changes go here.** |
| `pages/` | Bro's own pages: new tab, `bro://history`, `bro://bookmarks`, `bro://downloads`, `bro://settings` |
| `MainWindow.xaml.cs` | Tabs, navigation, windows, shortcuts, and the bridge between the UI and Chromium |
| `MainWindow.Pages.cs` | Messages between Bro's pages and the app (history, bookmarks, settings…) |
| `MainWindow.Suggest.cs` | Address bar suggestions from history and bookmarks |
| `MainWindow.AdBlock.cs` | Hooks the ad blocker into each tab |
| `AdBlock/` | Ad blocker: EasyList/EasyPrivacy download, rule parser and matcher, element hiding |
| `Core/` | Saved data: settings, bookmarks, history, downloads, site icons, last session |
| `Native.cs` | Win32 helpers for the borderless window, full screen and rounded page corners |
| `Uninstaller.cs` | `Bro.exe --uninstall` |
| `installer/` | `bro-setup.exe` source and `build.ps1` |

The page area always follows the `#viewport` element in `ui/style.css`, so you can move or resize it freely.
If you change its corner radius, also change `PageCornerRadius` in `MainWindow.xaml.cs`.

Your data lives in `%LOCALAPPDATA%\Bro` (set `BRO_DATA_DIR` to use another folder, e.g. for testing).

## Ad blocker

- Blocks requests matching **EasyList** (ads) and **EasyPrivacy** (trackers), downloaded on first run and
  refreshed every 4 days into `%LOCALAPPDATA%\Bro\filters`. Matching happens on your PC.
- Hides leftover ad boxes with the lists' element-hiding rules.
- The **shield** in the toolbar shows how many requests were blocked; click it to pause blocking on that site.
- Not supported: regex rules, `$redirect`/`$csp`/`$removeparam` and other advanced options, scriptlets.
  YouTube video ads are not reliably blocked.

## Import, export and manual sync

Settings › Import and export (`bro://settings#import`):

- **From another browser:** Chrome, Edge, Brave, Vivaldi, Opera, Chromium and Firefox profiles found on this PC.
  Imports bookmarks (into a folder named after the browser) and the last 90 days of history. Running it twice
  doesn't create duplicates. Passwords and cookies can't be imported: browsers encrypt them for themselves.
- **Bookmarks file:** import or export the standard bookmarks `.html` file every browser understands.
- **Back up and sync:** export one `.json` file with bookmarks, history and settings; import it in Bro on another
  PC. Importing only adds, it never deletes, so you can go back and forth.

## Private windows

Ctrl+Shift+N. Uses an InPrivate profile: no history, and cookies/site data are wiped when the last
private window closes. Bookmarks and downloads made there are kept.

## Shortcuts

Ctrl+T new tab · Ctrl+W close tab · Ctrl+Shift+T reopen closed tab · Ctrl+N new window ·
Ctrl+Shift+N private window · Ctrl+Tab / Ctrl+Shift+Tab switch tabs · Ctrl+1…9 jump to a tab ·
Ctrl+L / Alt+D / F6 address bar · Ctrl+D bookmark page · Ctrl+Shift+O bookmarks · Ctrl+H history ·
Ctrl+J downloads · F5 / Ctrl+R reload · Alt+Left / Alt+Right back / forward · F11 full screen · F12 DevTools
