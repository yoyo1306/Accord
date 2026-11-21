using System.ComponentModel;
using System.Diagnostics;
using Accord.Core.Audio;
using Accord.Core.Models;
using Accord.Core.SystemIntegration;

namespace Accord.Core.Ipc;

/// <summary>
/// Manages on-demand elevation of Accord.Admin.exe for privileged registry / service / scheduled-task actions.
/// Keeps a single elevated session alive until Accord.exe closes (so UAC is prompted at most once per session).
/// If Accord.exe is already elevated, executes operations directly in-process.
/// </summary>
public sealed class AdminClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _adminProcess;
    private string _pipeName = string.Empty;

    public bool IsSessionActive =>
        AudioRegistryService.IsCurrentProcessElevated() ||
        (_adminProcess is not null && !_adminProcess.HasExited);

    public async Task<AdminResponse> ExecuteAsync(AdminRequest request, CancellationToken cancellationToken = default)
    {
        if (AudioRegistryService.IsCurrentProcessElevated())
        {
            return await Task.Run(() => ExecuteDirectly(request), cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_adminProcess is null || _adminProcess.HasExited)
            {
                bool started = await StartElevatedWorkerAsync(cancellationToken).ConfigureAwait(false);
                if (!started)
                {
                    return new AdminResponse
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Message = "Élévation administrateur annulée ou impossible."
                    };
                }
            }

            return await AdminProtocol.SendViaPipeOrFileAsync(
                _pipeName,
                request,
                TimeSpan.FromSeconds(45),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartElevatedWorkerAsync(CancellationToken cancellationToken)
    {
        string adminExe = AppPaths.AdminExePath;
        if (!File.Exists(adminExe))
        {
            return false;
        }

        int pid = Environment.ProcessId;
        int sessionId = Process.GetCurrentProcess().SessionId;
        _pipeName = AdminProtocol.BuildPipeName(sessionId, pid);

        var psi = new ProcessStartInfo
        {
            FileName = adminExe,
            Arguments = $"--pipe \"{_pipeName}\" --parent-pid {pid} --data-dir \"{AppPaths.DataDir}\"",
            WorkingDirectory = AppPaths.AppDir,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            _adminProcess = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223 /* ERROR_CANCELLED */)
        {
            return false;
        }

        if (_adminProcess is null) return false;

        // Wait up to 8 seconds for the elevated server to respond to Ping.
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_adminProcess.HasExited) return false;

            try
            {
                AdminResponse ping = await AdminProtocol.SendViaPipeOrFileAsync(
                    _pipeName,
                    new AdminRequest { Action = "Ping" },
                    TimeSpan.FromMilliseconds(900),
                    cancellationToken).ConfigureAwait(false);
                if (ping.Success) return true;
            }
            catch
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public static AdminResponse ExecuteDirectly(AdminRequest req)
    {
        try
        {
            switch (req.Action)
            {
                case "Ping":
                    return new AdminResponse { RequestId = req.RequestId, Success = true, Message = "OK" };

                case "Rename":
                    AudioRegistryService.Rename(req.Flow, req.DeviceId, req.Name, req.Description);
                    return new AdminResponse
                    {
                        RequestId = req.RequestId,
                        Success = true,
                        Message = $"Renommé : {req.Name}" + (string.IsNullOrWhiteSpace(req.Description) ? "" : $" ({req.Description})")
                    };

                case "RestoreSingle":
                    if (req.Backup is null)
                    {
                        return new AdminResponse { RequestId = req.RequestId, Success = false, Message = "Sauvegarde manquante." };
                    }
                    AudioRegistryService.RestoreSingle(req.Backup);
                    return new AdminResponse
                    {
                        RequestId = req.RequestId,
                        Success = true,
                        Message = $"Restauré : {req.Backup.SavedDisplay}"
                    };

                case "RestoreAll":
                {
                    BootRestoreResult res = AudioRegistryService.RestoreAllFromBackup(testOnly: false);
                    AppPaths.AppendLog(AppPaths.AutoRestoreLogPath, $"manual applied={res.Applied} failed={res.Failed}");
                    return new AdminResponse
                    {
                        RequestId = req.RequestId,
                        Success = res.Failed == 0,
                        AppliedCount = res.Applied,
                        FailedCount = res.Failed,
                        Message = res.Failed == 0
                            ? $"{res.Applied} périphérique(s) restauré(s)."
                            : $"{res.Applied} restauré(s), {res.Failed} échec(s)."
                    };
                }

                case "SetBootRestore":
                    ScheduledTaskManager.SetBootRestoreEnabled(req.Enabled);
                    return new AdminResponse
                    {
                        RequestId = req.RequestId,
                        Success = true,
                        Message = req.Enabled
                            ? "Restauration au démarrage activée."
                            : "Restauration au démarrage désactivée."
                    };

                default:
                    return new AdminResponse
                    {
                        RequestId = req.RequestId,
                        Success = false,
                        Message = $"Action inconnue : {req.Action}"
                    };
            }
        }
        catch (Exception ex)
        {
            return new AdminResponse
            {
                RequestId = req.RequestId,
                Success = false,
                Message = ex.Message
            };
        }
    }

    public void Dispose()
    {
        if (_adminProcess is not null && !_adminProcess.HasExited)
        {
            try
            {
                _ = AdminProtocol.SendViaPipeOrFileAsync(
                    _pipeName,
                    new AdminRequest { Action = "Shutdown" },
                    TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
            }
            catch { }
        }
        _adminProcess?.Dispose();
        _gate.Dispose();
    }
}
