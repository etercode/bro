using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BroSetup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // bro-setup.exe --silent [--no-desktop]: installs without any window (exit code 0 = success)
        if (args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
            return SetupWindow.InstallSilently(desktopShortcut: !args.Any(a => a.Equals("--no-desktop", StringComparison.OrdinalIgnoreCase)));
        var app = new Application();
        app.Run(new SetupWindow());
        return 0;
    }
}

/// <summary>Installs Bro for the current user into %LOCALAPPDATA%\Programs\Bro. No admin rights needed.</summary>
sealed class SetupWindow : Window
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bro";
    const string Marker = "bro.installed"; // the app's uninstaller only deletes folders that have this

    static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Bro");
    static string ExePath => Path.Combine(InstallDir, "Bro.exe");

    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
    static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x1E, 0x66, 0xF5));

    readonly TextBlock _status;
    readonly ProgressBar _progress;
    readonly CheckBox _desktop;
    readonly Button _primary;
    readonly Button _secondary;
    bool _installed;

    public SetupWindow()
    {
        Title = "Install bro!";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;

        var icon = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/logo-256.png")),
            Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left,
        };
        var heading = new TextBlock
        {
            Text = "Install bro!", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        };
        var info = new TextBlock
        {
            Text = $"bro! will be installed for you only, in\n{InstallDir}",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
        };
        _desktop = new CheckBox
        {
            Content = "Add a shortcut to the desktop", IsChecked = true, Foreground = Text, Margin = new Thickness(0, 20, 0, 0),
        };
        _progress = new ProgressBar
        {
            Height = 6, Margin = new Thickness(0, 22, 0, 0), Visibility = Visibility.Collapsed,
            Foreground = Accent, Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x34)), BorderThickness = new Thickness(0),
        };
        _status = new TextBlock { Foreground = Muted, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

        _primary = MakeButton("Install", primary: true);
        _secondary = MakeButton("Cancel", primary: false);
        _primary.Click += async (_, _) => { if (_installed) Launch(); else await InstallAsync(); };
        _secondary.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0),
        };
        buttons.Children.Add(_secondary);
        buttons.Children.Add(_primary);

        var panel = new StackPanel { Margin = new Thickness(36, 32, 36, 28) };
        foreach (var child in new UIElement[] { icon, heading, info, _desktop, _progress, _status, buttons }) panel.Children.Add(child);
        Content = panel;

        if (File.Exists(ExePath))
        {
            heading.Text = "Update bro!";
            _primary.Content = "Update";
            info.Text = $"bro! is already installed in\n{InstallDir}\nYour bookmarks, history and settings are kept.";
        }
    }

    static Button MakeButton(string label, bool primary)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.PaddingProperty, new Thickness(20, 8, 20, 9));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        return new Button
        {
            Content = label, Template = template, Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2F)),
            Foreground = primary ? Brushes.White : Text, Margin = new Thickness(10, 0, 0, 0), MinWidth = 96,
        };
    }

    async Task InstallAsync()
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
        if (payload == null)
        {
            _status.Text = "This installer was built without the app inside it. Run installer\\build.ps1 to make a complete one.";
            return;
        }
        if (Directory.Exists(InstallDir) && !File.Exists(Path.Combine(InstallDir, Marker)) &&
            Directory.EnumerateFileSystemEntries(InstallDir).Any())
        {
            _status.Text = $"{InstallDir} already exists and wasn't made by this installer. Move or delete it, then try again.";
            return;
        }
        if (!CloseRunningBro()) return;

        _primary.IsEnabled = _secondary.IsEnabled = _desktop.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        var desktop = _desktop.IsChecked == true;
        var progress = new Progress<double>(p => { _progress.Value = p * 100; _status.Text = $"Copying files… {p:P0}"; });
        try
        {
            await Task.Run(() => Install(payload, progress, desktop));
            _installed = true;
            _status.Text = "bro! is installed. You'll find it in the Start menu.";
            _primary.Content = "Open bro!";
            _secondary.Content = "Close";
            _progress.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = "Installing failed: " + e.Message;
        }
        finally
        {
            _primary.IsEnabled = _secondary.IsEnabled = true;
        }
    }

    public static int InstallSilently(bool desktopShortcut)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
        if (payload == null) return 2;
        if (Directory.Exists(InstallDir) && !File.Exists(Path.Combine(InstallDir, Marker)) &&
            Directory.EnumerateFileSystemEntries(InstallDir).Any()) return 3;
        foreach (var p in RunningInstalledBro())
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try
        {
            Install(payload, new Progress<double>(), desktopShortcut);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 1;
        }
    }

    static System.Collections.Generic.List<Process> RunningInstalledBro() =>
        Process.GetProcessesByName("Bro").Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }).ToList();

    bool CloseRunningBro()
    {
        var running = RunningInstalledBro();
        if (running.Count == 0) return true;
        if (MessageBox.Show(this, "bro! is open. Close it to continue?", "Install bro!",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
        foreach (var p in running)
        {
            try { p.CloseMainWindow(); if (!p.WaitForExit(4000)) p.Kill(); p.WaitForExit(3000); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return true;
    }

    static void Install(Stream payload, IProgress<double> progress, bool desktopShortcut)
    {
        // Replace the previous version's files (user data lives elsewhere, in %LOCALAPPDATA%\Bro)
        if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true);
        Directory.CreateDirectory(InstallDir);

        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var root = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
            var total = Math.Max(1, zip.Entries.Count);
            for (int i = 0; i < zip.Entries.Count; i++)
            {
                var entry = zip.Entries[i];
                var target = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the folder
                if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (i % 10 == 0) progress.Report((double)i / total);
            }
        }
        File.WriteAllText(Path.Combine(InstallDir, Marker), "Installed by bro-setup. Uninstall from Windows Settings > Apps.");
        progress.Report(1);

        var icon = WriteIconFile();
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        // Shortcuts from earlier versions (the app was called "Bro")
        foreach (var old in new[] { Path.Combine(programs, "Bro.lnk"), Path.Combine(desktop, "Bro.lnk") })
            if (File.Exists(old)) File.Delete(old);

        CreateShortcut(Path.Combine(programs, ShortcutName), icon);
        var desktopLink = Path.Combine(desktop, ShortcutName);
        if (desktopShortcut) CreateShortcut(desktopLink, icon);
        else if (File.Exists(desktopLink)) File.Delete(desktopLink);

        Register(icon);
        RefreshShellIcons();
    }

    const string ShortcutName = "bro!.lnk";

    /// <summary>
    /// Writes the app icon as its own file, named after its contents. Windows caches icons by file path,
    /// so a new name makes shortcuts show a changed logo right away instead of the cached old one.
    /// </summary>
    static string WriteIconFile()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        if (stream == null) return ExePath + ",0";
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(bytes), 0, 4).Replace("-", "").ToLowerInvariant();
        var path = Path.Combine(InstallDir, $"bro-{hash}.ico");
        File.WriteAllBytes(path, bytes);
        return path + ",0";
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tells Explorer, the Start menu and the taskbar to reload icons.</summary>
    static void RefreshShellIcons()
    {
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var ie4uinit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ie4uinit.exe");
            if (File.Exists(ie4uinit))
                Process.Start(new ProcessStartInfo(ie4uinit, "-show") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    static void CreateShortcut(string path, string icon)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = ExePath;
        link.WorkingDirectory = InstallDir;
        link.IconLocation = icon;
        link.Description = "bro! browser";
        link.Save();
    }

    /// <summary>Lists Bro under Settings > Apps, with "Uninstall" running Bro.exe --uninstall.</summary>
    static void Register(string icon)
    {
        var version = FileVersionInfo.GetVersionInfo(ExePath);
        long sizeKb = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "bro!");
        key.SetValue("DisplayVersion", $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}");
        key.SetValue("Publisher", "bro!");
        key.SetValue("DisplayIcon", icon);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    void Launch()
    {
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
        Close();
    }
}
