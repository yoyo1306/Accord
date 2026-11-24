using System.Diagnostics;

namespace Accord.Core.SystemIntegration;

/// <summary>
/// Manages the Windows Scheduled Task "AccordRestauration" that invokes Accord.Admin.exe --restore-boot at logon.
/// Automatically detects legacy v1.x tasks pointing to Start-RestoreAudioNames.vbs / wscript.exe.
/// </summary>
public static class ScheduledTaskManager
{
    public const string TaskName = "AccordRestauration";

    public static bool IsBootRestoreEnabled()
    {
        (int code, string xml) = RunSchtasksCapture($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0 || string.IsNullOrWhiteSpace(xml)) return false;

        // Treat legacy v1.x VBS/PowerShell tasks as NOT configured for Accord v2
        if (IsLegacyTaskXml(xml)) return false;

        return xml.Contains("Accord.Admin.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasLegacyBootRestoreTask()
    {
        (int code, string xml) = RunSchtasksCapture($"/Query /TN \"{TaskName}\" /XML");
        return code == 0 && !string.IsNullOrWhiteSpace(xml) && IsLegacyTaskXml(xml);
    }

    public static void SetBootRestoreEnabled(bool enabled)
    {
        if (enabled)
        {
            string adminExe = AppPaths.AdminExePath;
            if (!File.Exists(adminExe))
            {
                throw new FileNotFoundException("Module administrateur introuvable.", adminExe);
            }

            string dataDir = AppPaths.DataDir;
            string taskRun = $"\\\"{adminExe}\\\" --restore-boot --data-dir \\\"{dataDir}\\\"";
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"{taskRun}\" /SC ONLOGON /RL HIGHEST /F");
            if (code != 0)
            {
                throw new InvalidOperationException($"Impossible de créer la tâche planifiée (code {code}).");
            }
        }
        else
        {
            if (RunSchtasks($"/Query /TN \"{TaskName}\"") != 0) return;
            int code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            if (code != 0)
            {
                throw new InvalidOperationException($"Impossible de supprimer la tâche planifiée (code {code}).");
            }
        }
    }

    private static bool IsLegacyTaskXml(string xml) =>
        xml.Contains("Start-RestoreAudioNames.vbs", StringComparison.OrdinalIgnoreCase) ||
        xml.Contains("Restore-AudioNames.ps1", StringComparison.OrdinalIgnoreCase) ||
        xml.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase);

    private static int RunSchtasks(string arguments)
    {
        (int code, _) = RunSchtasksCapture(arguments);
        return code;
    }

    private static (int ExitCode, string StdOut) RunSchtasksCapture(string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        proc.Start();
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10000);
        return (proc.HasExited ? proc.ExitCode : -1, stdout);
    }
}
