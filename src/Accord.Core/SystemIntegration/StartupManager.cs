using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Accord.Core.SystemIntegration;

/// <summary>
/// Manages the background Accord.Watcher.exe startup shortcut (Startup\Accord.lnk)
/// via native COM IShellLinkW (zero PowerShell / WScript) and watcher process lifecycle.
/// </summary>
public static class StartupManager
{
    private static string StartupDir => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    public static string ShortcutPath => Path.Combine(StartupDir, "Accord.lnk");
    private static string LegacyShortcutPath => Path.Combine(StartupDir, "AudioRename Bascule.lnk");

    public static bool IsAutoSwitchEnabled()
    {
        if (!File.Exists(ShortcutPath)) return false;
        try
        {
            string? target = ReadShortcutTarget(ShortcutPath);
            if (string.IsNullOrWhiteSpace(target)) return false;
            return target.EndsWith("Accord.Watcher.exe", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(target);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsWatcherRunning() => GetWatcherProcesses().Count > 0;

    public static void EnableAutoSwitch()
    {
        string watcherExe = AppPaths.WatcherExePath;
        if (!File.Exists(watcherExe))
        {
            throw new FileNotFoundException("Exécutable de surveillance introuvable.", watcherExe);
        }

        CreateShortcut(ShortcutPath, watcherExe, AppPaths.AppDir, "Accord — Bascule automatique du son");
        TryDelete(LegacyShortcutPath);

        if (!IsWatcherRunning())
        {
            StartWatcher();
        }
    }

    public static void DisableAutoSwitch()
    {
        TryDelete(ShortcutPath);
        TryDelete(LegacyShortcutPath);
        StopWatchers();
    }

    public static void StartWatcher()
    {
        string watcherExe = AppPaths.WatcherExePath;
        if (!File.Exists(watcherExe) || IsWatcherRunning()) return;

        var psi = new ProcessStartInfo
        {
            FileName = watcherExe,
            WorkingDirectory = AppPaths.AppDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    public static void StopWatchers()
    {
        foreach (Process p in GetWatcherProcesses())
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(1500);
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static List<Process> GetWatcherProcesses()
    {
        var list = new List<Process>();
        foreach (Process p in Process.GetProcessesByName("Accord.Watcher"))
        {
            list.Add(p);
        }
        return list;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string? ReadShortcutTarget(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var persist = (IPersistFile)link;
            persist.Load(shortcutPath, 0);
            var sb = new System.Text.StringBuilder(512);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            link.SetShowCmd(7 /* SW_SHOWMINNOACTIVE */);

            var persist = (IPersistFile)link;
            persist.Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
