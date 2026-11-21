using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Accord.Core.Models;

namespace Accord.Core.Ipc;

public sealed class AdminRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = string.Empty; // "Ping", "Rename", "RestoreSingle", "RestoreAll", "SetBootRestore", "Shutdown"
    public string Flow { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DeviceBackup? Backup { get; set; }
    public bool Enabled { get; set; }
}

public sealed class AdminResponse
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int AppliedCount { get; set; }
    public int FailedCount { get; set; }
}

/// <summary>
/// Named Pipe IPC secured with AuthenticatedUserSid (ReadWrite) + automatic file-based fallback
/// for communication between standard-user Accord.exe and elevated Accord.Admin.exe.
/// </summary>
public static class AdminProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string BuildPipeName(int sessionId, int parentPid) =>
        $"Accord.Admin.{sessionId}.{parentPid}";

    public static NamedPipeServerStream CreateSecuredServerPipe(string pipeName)
    {
        var pipeSecurity = new PipeSecurity();

        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            authUsers,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            admins,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 8192,
            outBufferSize: 8192,
            pipeSecurity);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    public static async Task<AdminResponse> SendViaPipeOrFileAsync(
        string pipeName,
        AdminRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendViaPipeAsync(pipeName, request, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception pipeEx) when (pipeEx is not OperationCanceledException)
        {
            // Automatic file fallback in DataDir\ipc\
            return await SendViaFileFallbackAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AdminResponse> SendViaPipeAsync(
        string pipeName,
        AdminRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        await client.ConnectAsync(cts.Token).ConfigureAwait(false);

        string payload = Serialize(request);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync(payload.AsMemory(), cts.Token).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("Réponse vide du module administrateur.");
        }

        return Deserialize<AdminResponse>(line)
            ?? new AdminResponse { RequestId = request.RequestId, Success = false, Message = "Réponse JSON invalide." };
    }

    private static async Task<AdminResponse> SendViaFileFallbackAsync(
        AdminRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.IpcDir);
        string reqPath = Path.Combine(AppPaths.IpcDir, $"req_{request.RequestId}.json");
        string resPath = Path.Combine(AppPaths.IpcDir, $"res_{request.RequestId}.json");
        string tmpPath = reqPath + ".tmp";

        File.WriteAllText(tmpPath, Serialize(request), new UTF8Encoding(false));
        File.Move(tmpPath, reqPath, overwrite: true);

        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(resPath))
                {
                    await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                    string json = await File.ReadAllTextAsync(resPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    try { File.Delete(resPath); } catch { }
                    return Deserialize<AdminResponse>(json)
                        ?? new AdminResponse { RequestId = request.RequestId, Success = false, Message = "Réponse fichier invalide." };
                }
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }

            return new AdminResponse
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "Délai d'attente dépassé lors de la communication avec le module administrateur."
            };
        }
        finally
        {
            try { if (File.Exists(reqPath)) File.Delete(reqPath); } catch { }
        }
    }
}
