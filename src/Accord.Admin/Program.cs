using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Accord.Core;
using Accord.Core.Audio;
using Accord.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Accord.Admin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? pipeName = null;
        string? dataDir = null;
        int parentPid = 0;
        bool restoreBoot = false;
        bool testOnly = false;
        bool mmsysOnly = false;
        string flow = string.Empty;
        string id = string.Empty;
        string name = string.Empty;
        string description = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (Eq(a, "--pipe") && i + 1 < args.Length) pipeName = args[++i];
            else if (Eq(a, "--data-dir") && i + 1 < args.Length) dataDir = args[++i];
            else if (Eq(a, "--parent-pid") && i + 1 < args.Length) int.TryParse(args[++i], out parentPid);
            else if (Eq(a, "--restore-boot")) restoreBoot = true;
            else if (Eq(a, "--test-only") || Eq(a, "-TestOnly")) testOnly = true;
            else if (Eq(a, "--mmsys-only")) mmsysOnly = true;
            else if (Eq(a, "--flow") && i + 1 < args.Length) flow = args[++i];
            else if (Eq(a, "--id") && i + 1 < args.Length) id = args[++i];
            else if (Eq(a, "--name") && i + 1 < args.Length) name = args[++i];
            else if (Eq(a, "--description") && i + 1 < args.Length) description = args[++i];
        }

        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            AppPaths.ConfigureOverride(dataDir);
        }
        AppPaths.EnsureInitialized();

        if (testOnly) AttachParentConsole();

        if (mmsysOnly)
        {
            try
            {
                string label = string.IsNullOrWhiteSpace(description) ? name : description;
                AudioCoreCom.SetInterfaceName(AudioRegistryService.BuildEndpointId(flow, id), label);
                return 0;
            }
            catch (Exception ex)
            {
                AppPaths.AppendLog(AppPaths.AutoRestoreLogPath, "MMSYS-ERROR: " + ex.Message);
                return 2;
            }
        }

        if (restoreBoot)
        {
            return RunBootRestore(testOnly);
        }

        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            RunIpcServerAsync(pipeName, parentPid).GetAwaiter().GetResult();
            return 0;
        }

        return 0;
    }

    private static int RunBootRestore(bool testOnly)
    {
        try
        {
            BootRestoreResult res = AudioRegistryService.RestoreAllFromBackup(testOnly: testOnly);
            if (testOnly)
            {
                foreach (string line in res.Lines) Console.WriteLine(line);
                Console.WriteLine($"applied={res.Applied} failed={res.Failed}");
                return res.Failed == 0 ? 0 : 2;
            }

            if (res.Applied == 0 && res.Failed == 0)
            {
                AppPaths.AppendLog(AppPaths.AutoRestoreLogPath, "no-backup");
                return 1;
            }

            AppPaths.AppendLog(AppPaths.AutoRestoreLogPath, $"applied={res.Applied} failed={res.Failed}");
            return res.Failed == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            if (testOnly) Console.WriteLine("ERROR: " + ex.Message);
            else AppPaths.AppendLog(AppPaths.AutoRestoreLogPath, "ERROR: " + ex.Message);
            return 2;
        }
    }

    private static async Task RunIpcServerAsync(string pipeName, int parentPid)
    {
        using var cts = new CancellationTokenSource();

        // Watch parent process exit so the elevated helper never lingers as an orphan.
        Task parentWatch = Task.Run(async () =>
        {
            if (parentPid <= 0) return;
            try
            {
                using Process parent = Process.GetProcessById(parentPid);
                await parent.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Parent already exited.
            }
            finally
            {
                cts.Cancel();
            }
        }, cts.Token);

        // Parallel file-based fallback watcher in <DataDir>\ipc\
        Task fileFallbackLoop = Task.Run(async () =>
        {
            Directory.CreateDirectory(AppPaths.IpcDir);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    foreach (string reqFile in Directory.GetFiles(AppPaths.IpcDir, "req_*.json"))
                    {
                        string json = await File.ReadAllTextAsync(reqFile, Encoding.UTF8, cts.Token).ConfigureAwait(false);
                        try { File.Delete(reqFile); } catch { }

                        AdminRequest? req = AdminProtocol.Deserialize<AdminRequest>(json);
                        if (req is null) continue;

                        if (string.Equals(req.Action, "Shutdown", StringComparison.OrdinalIgnoreCase))
                        {
                            WriteFileResponse(new AdminResponse { RequestId = req.RequestId, Success = true, Message = "Bye" });
                            cts.Cancel();
                            return;
                        }

                        AdminResponse resp = AdminClient.ExecuteDirectly(req);
                        WriteFileResponse(resp);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient IO race conditions.
                }

                try { await Task.Delay(120, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, cts.Token);

        // Main Named Pipe server loop
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream server = AdminProtocol.CreateSecuredServerPipe(pipeName);
                await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;

                AdminRequest? req = AdminProtocol.Deserialize<AdminRequest>(line);
                if (req is null)
                {
                    await writer.WriteLineAsync(AdminProtocol.Serialize(new AdminResponse
                    {
                        Success = false,
                        Message = "Requête invalide."
                    })).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(req.Action, "Shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync(AdminProtocol.Serialize(new AdminResponse
                    {
                        RequestId = req.RequestId,
                        Success = true,
                        Message = "Arrêt."
                    })).ConfigureAwait(false);
                    cts.Cancel();
                    break;
                }

                AdminResponse response = AdminClient.ExecuteDirectly(req);
                await writer.WriteLineAsync(AdminProtocol.Serialize(response)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }

        try { await Task.WhenAll(parentWatch, fileFallbackLoop).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch { }
    }

    private static void WriteFileResponse(AdminResponse resp)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.IpcDir);
            string resPath = Path.Combine(AppPaths.IpcDir, $"res_{resp.RequestId}.json");
            string tmpPath = resPath + ".tmp";
            File.WriteAllText(tmpPath, AdminProtocol.Serialize(resp), new UTF8Encoding(false));
            File.Move(tmpPath, resPath, overwrite: true);
        }
        catch { }
    }

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void AttachParentConsole()
    {
        IntPtr existing = GetStdHandle(-11);
        if (existing != IntPtr.Zero && existing != new IntPtr(-1))
        {
            Console.SetOut(new StreamWriter(new FileStream(new SafeFileHandle(existing, false), FileAccess.Write)) { AutoFlush = true });
            return;
        }
        if (!AttachConsole(-1)) return;
        IntPtr handle = CreateFile("CONOUT$", 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return;
        SetStdHandle(-11, handle);
        Console.SetOut(new StreamWriter(new FileStream(new SafeFileHandle(handle, true), FileAccess.Write)) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);
}
