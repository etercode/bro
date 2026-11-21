using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Bro.Core;

namespace Bro;

/// <summary>"Bro.exe --uninstall", run from Windows' Installed apps list. Undoes what bro-setup did.</summary>
static class Uninstaller
{
    public const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bro";
    public const string Marker = "bro.installed"; // written by the installer; protects any other folder

    /// <param name="quiet">No questions or messages; keeps user data (Bro.exe --uninstall --quiet).</param>
    public static void Run(bool quiet = false)
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        if (!File.Exists(Path.Combine(dir, Marker)))
        {
            if (quiet) return;
            MessageBox.Show("This copy of bro! wasn't installed with bro-setup, so there is nothing to uninstall.",
                            "Uninstall bro!", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!quiet && MessageBox.Show("Remove bro! from this PC?", "Uninstall bro!",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var deleteData = !quiet && MessageBox.Show(
            "Also delete your bookmarks, history, settings and saved logins?\n\nChoose No to keep them for a future install.",
            "Uninstall bro!", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Close the installed copy if it's open (not other copies, e.g. a development build)
        foreach (var p in Process.GetProcessesByName("Bro").Where(p => p.Id != Environment.ProcessId))
        {
            try
            {
                if (!string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), dir, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        foreach (var shortcut in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Bro.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Bro.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "bro!.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "bro!.lnk"),
                 })
        {
            try { File.Delete(shortcut); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, throwOnMissingSubKey: false); }
        catch (UnauthorizedAccessException) { }

        // The running exe can't delete its own folder, so a short-lived cmd does it once we've exited.
        var command = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"";
        if (deleteData) command += $" & rmdir /s /q \"{Services.DataDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command)
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        });
        if (!quiet) MessageBox.Show("bro! has been removed.", "Uninstall bro!", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
