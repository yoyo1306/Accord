using System.Diagnostics;

namespace Accord.Core.SystemIntegration;

/// <summary>
/// Manages the Windows Scheduled Task "AccordRestauration" that invokes Accord.Admin.exe --restore-boot at logon.
/// </summary>
public static class ScheduledTaskManager
{
    public const string TaskName = "AccordRestauration";

    public static bool IsBootRestoreEnabled()
    {
        return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
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
            if (!IsBootRestoreEnabled()) return;
            int code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            if (code != 0)
            {
                throw new InvalidOperationException($"Impossible de supprimer la tâche planifiée (code {code}).");
            }
        }
    }

    private static int RunSchtasks(string arguments)
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
        proc.WaitForExit(10000);
        return proc.HasExited ? proc.ExitCode : -1;
    }
}
