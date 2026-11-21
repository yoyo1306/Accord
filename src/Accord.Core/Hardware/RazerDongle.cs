using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Accord.Core.Hardware;

public enum RazerHeadsetState
{
    Disconnected = 0,
    Connected = 1,
    Unknown = 2
}

/// <summary>
/// Queries the Razer BlackShark V3 Pro HyperSpeed 2.4 GHz USB dongle (VID 1532 / PID 0577 / col04)
/// using the same 64-byte wireless power-status HID report as Synapse getWirelessConnectionStatus.
/// </summary>
public static class RazerDongle
{
    private const int ReportLength = 64;
    private const int IoTimeoutMs = 450;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static RazerHeadsetState GetWirelessState()
    {
        try
        {
            string? path = FindDongleControlPath();
            if (string.IsNullOrEmpty(path))
            {
                return RazerHeadsetState.Disconnected;
            }

            return QueryPowerState(path);
        }
        catch
        {
            return RazerHeadsetState.Unknown;
        }
    }

    private static byte[] CreatePowerStatusQuery()
    {
        byte[] report = new byte[ReportLength];
        report[0] = 2;
        report[2] = 0x60;
        report[6] = 4;
        report[10] = 0x20;
        report[ReportLength - 2] = CalculateChecksum(report);
        return report;
    }

    private static RazerHeadsetState ParsePowerStatusResponse(byte[] response, int count)
    {
        if (count < ReportLength ||
            response[0] != 2 ||
            response[1] != 2 ||
            response[2] != 0x60 ||
            response[10] != 0x20 ||
            response[11] != 1 ||
            response[ReportLength - 2] != CalculateChecksum(response))
        {
            return RazerHeadsetState.Unknown;
        }

        return response[13] switch
        {
            1 => RazerHeadsetState.Connected,
            0 => RazerHeadsetState.Disconnected,
            _ => RazerHeadsetState.Unknown
        };
    }

    private static RazerHeadsetState QueryPowerState(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            0x80000000u | 0x40000000u, // GENERIC_READ | GENERIC_WRITE
            0x00000001u | 0x00000002u, // FILE_SHARE_READ | FILE_SHARE_WRITE
            IntPtr.Zero,
            3u,                        // OPEN_EXISTING
            0x40000000u,               // FILE_FLAG_OVERLAPPED
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return RazerHeadsetState.Unknown;
        }

        using var stream = new FileStream(handle, FileAccess.ReadWrite, ReportLength, isAsync: true);
        using var cts = new CancellationTokenSource(IoTimeoutMs);

        byte[] query = CreatePowerStatusQuery();
        try
        {
            stream.WriteAsync(query.AsMemory(0, query.Length), cts.Token).AsTask().GetAwaiter().GetResult();

            byte[] response = new byte[ReportLength];
            while (!cts.IsCancellationRequested)
            {
                int count = stream.ReadAsync(response.AsMemory(0, response.Length), cts.Token).AsTask().GetAwaiter().GetResult();
                RazerHeadsetState state = ParsePowerStatusResponse(response, count);
                if (state != RazerHeadsetState.Unknown)
                {
                    return state;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return RazerHeadsetState.Unknown;
        }

        return RazerHeadsetState.Unknown;
    }

    private static byte CalculateChecksum(byte[] report)
    {
        byte checksum = 0;
        for (int index = 0; index < ReportLength - 2; index++)
        {
            checksum ^= report[index];
        }
        return checksum;
    }

    private static string? FindDongleControlPath()
    {
        foreach (string path in EnumerateHidPaths())
        {
            if (path.Contains("vid_1532", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("pid_0577", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("col04", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateHidPaths()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr infoSet = SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            0x00000002u | 0x00000010u /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);

        if (infoSet == InvalidHandleValue) yield break;

        try
        {
            uint index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                };

                if (!SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index++, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == 259 /* ERROR_NO_MORE_ITEMS */) break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                if (requiredSize == 0) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, detail, requiredSize, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (!string.IsNullOrEmpty(path)) yield return path;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid interfaceClassGuid;
        public int flags;
        public UIntPtr reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr infoSet,
        IntPtr deviceInfo,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA interfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr infoSet,
        ref SP_DEVICE_INTERFACE_DATA interfaceData,
        IntPtr detailData,
        uint detailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string path,
        uint access,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flags,
        IntPtr templateFile);
}
