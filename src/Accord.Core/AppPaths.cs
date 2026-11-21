using System.Text;

namespace Accord.Core;

/// <summary>
/// Resolves application data and configuration paths transparently for both
/// portable folders (writable beside the executable) and Program Files installs
/// (shared %ProgramData%\Accord so standard-user UI, Watcher, and elevated Admin helper share state).
/// </summary>
public static class AppPaths
{
    public const string AppName = "Accord";
    public const string BackupFileName = "Accord.backup.txt";
    public const string SettingsFileName = "settings.json";
    public const string WatcherStatusFileName = "watcher-status.json";

    private static string? _overrideDataDir;
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static string _dataDir = string.Empty;
    private static bool _isPortable;

    public static string AppDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool IsPortable
    {
        get
        {
            EnsureInitialized();
            return _isPortable;
        }
    }

    public static string DataDir
    {
        get
        {
            EnsureInitialized();
            return _dataDir;
        }
    }

    public static string SettingsPath => Path.Combine(DataDir, SettingsFileName);
    public static string BackupPath => Path.Combine(DataDir, BackupFileName);
    public static string WatcherStatusPath => Path.Combine(DataDir, WatcherStatusFileName);
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string WatcherLogPath => Path.Combine(LogsDir, "watcher.log");
    public static string AutoRestoreLogPath => Path.Combine(LogsDir, "autorestore.log");
    public static string IpcDir => Path.Combine(DataDir, "ipc");

    public static string WatcherExePath => Path.Combine(AppDir, "Accord.Watcher.exe");
    public static string AdminExePath => Path.Combine(AppDir, "Accord.Admin.exe");
    public static string AppExePath => Path.Combine(AppDir, "Accord.exe");

    public static void ConfigureOverride(string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return;
        lock (InitLock)
        {
            _overrideDataDir = Path.GetFullPath(dataDir);
            _initialized = false;
        }
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;

            if (!string.IsNullOrWhiteSpace(_overrideDataDir))
            {
                _dataDir = _overrideDataDir!;
                _isPortable = string.Equals(_dataDir, AppDir, StringComparison.OrdinalIgnoreCase);
            }
            else if (IsUnderProgramFiles(AppDir) || !CanWriteToDirectory(AppDir))
            {
                _dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    AppName);
                _isPortable = false;
            }
            else
            {
                _dataDir = AppDir;
                _isPortable = true;
            }

            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(Path.Combine(_dataDir, "logs"));

            if (!_isPortable)
            {
                MigrateDefaultIfMissing(SettingsFileName);
                MigrateDefaultIfMissing(BackupFileName);
            }

            _initialized = true;
        }
    }

    private static void MigrateDefaultIfMissing(string fileName)
    {
        try
        {
            string target = Path.Combine(_dataDir, fileName);
            if (File.Exists(target)) return;

            string source = Path.Combine(AppDir, fileName);
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: false);
            }
        }
        catch
        {
            // Non-fatal: defaults will be created on demand.
        }
    }

    private static bool IsUnderProgramFiles(string path)
    {
        string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrEmpty(pf))
        {
            string normPf = Path.GetFullPath(pf).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(normPf, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (!string.IsNullOrEmpty(pf86))
        {
            string normPf86 = Path.GetFullPath(pf86).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(normPf86, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool CanWriteToDirectory(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".accord_write_probe_{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok", Encoding.UTF8);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void AppendLog(string logPath, string message, long maxBytes = 512 * 1024)
    {
        try
        {
            string? dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(logPath) && new FileInfo(logPath).Length > maxBytes)
            {
                string old = logPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(logPath, old);
            }

            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line, new UTF8Encoding(false));
        }
        catch
        {
            // Logging must never crash background or UI flows.
        }
    }
}
